using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using VelQuery.Generators;

namespace VelQuery.Testing;

/// <summary>Builds in-memory compilations against the real VelQuery assemblies and runs the generator over them.</summary>
internal static class GeneratorHarness
{
    public static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static readonly Lazy<ImmutableArray<MetadataReference>> s_references = new(() =>
    {
        // The test host's trusted platform assemblies are the .NET 10 reference set, available offline.
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Concat([typeof(QueryDescriptor<>).Assembly.Location, typeof(VelQuerySession).Assembly.Location])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.Ordinal);
        return [.. paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))];
    });

    public static CSharpCompilation Compile(params string[] sources)
        => Compile(sources.Select((s, i) => (s, $"/src/Input{i}.cs")).ToArray());

    public static CSharpCompilation Compile(params (string Source, string Path)[] sources)
        => CSharpCompilation.Create(
            "VelQuery.TestInput",
            sources.Select(s => CSharpSyntaxTree.ParseText(s.Source, ParseOptions, s.Path)),
            s_references.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    public static GeneratorDriver CreateDriver(bool trackSteps = false)
        => CSharpGeneratorDriver.Create(
            [new VelQuerySqlGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: trackSteps));

    /// <summary>Runs the generator and returns its result plus the updated compilation.</summary>
    public static (GeneratorRunResult Result, Compilation Output) Run(CSharpCompilation compilation)
    {
        var driver = CreateDriver().RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return (driver.GetRunResult().Results.Single(), output);
    }

    /// <summary>Generator diagnostics plus analyzer diagnostics over the generated compilation.</summary>
    public static IReadOnlyList<Diagnostic> GetVelQueryDiagnostics(CSharpCompilation compilation, params DiagnosticAnalyzer[] analyzers)
    {
        var (result, output) = Run(compilation);
        var diagnostics = new List<Diagnostic>(result.Diagnostics);
        if (result.Exception is not null)
        {
            throw new InvalidOperationException("Generator threw.", result.Exception);
        }

        if (analyzers.Length > 0)
        {
            diagnostics.AddRange(output.WithAnalyzers([.. analyzers]).GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult());
        }

        return diagnostics;
    }

    /// <summary>Compiler errors and warnings in a compilation, formatted for assertion messages.</summary>
    public static string[] CompilerProblems(Compilation compilation)
        => compilation.GetDiagnostics()
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(d => d.ToString())
            .ToArray();
}
