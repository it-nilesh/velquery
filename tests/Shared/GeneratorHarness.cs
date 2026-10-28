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

    public static GeneratorDriver CreateDriver(bool trackSteps = false, string? dialect = null, params (string Path, string Text)[] additionalFiles)
        => CSharpGeneratorDriver.Create(
            [new VelQuerySqlGenerator().AsSourceGenerator()],
            additionalTexts: [.. additionalFiles.Select(f => (AdditionalText)new InMemoryAdditionalText(f.Path, f.Text))],
            parseOptions: ParseOptions,
            optionsProvider: dialect is null ? null : new DialectOptionsProvider(dialect),
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: trackSteps));

    /// <summary>Runs the generator and returns its result plus the updated compilation.</summary>
    /// <param name="compilation">The input compilation.</param>
    /// <param name="dialect">Value of the <c>VelQuerySqlDialect</c> MSBuild property, or null when unset.</param>
    /// <param name="additionalFiles">Additional files, for example a <c>*.vqschema.json</c> schema snapshot.</param>
    public static (GeneratorRunResult Result, Compilation Output) Run(CSharpCompilation compilation, string? dialect = null, params (string Path, string Text)[] additionalFiles)
    {
        var driver = CreateDriver(dialect: dialect, additionalFiles: additionalFiles).RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return (driver.GetRunResult().Results.Single(), output);
    }

    /// <summary>Generator diagnostics plus analyzer diagnostics over the generated compilation.</summary>
    public static IReadOnlyList<Diagnostic> GetVelQueryDiagnostics(CSharpCompilation compilation, params DiagnosticAnalyzer[] analyzers)
        => GetVelQueryDiagnostics(compilation, null, analyzers);

    /// <summary>Generator diagnostics plus analyzer diagnostics for a project with the given SQL dialect.</summary>
    public static IReadOnlyList<Diagnostic> GetVelQueryDiagnostics(CSharpCompilation compilation, string? dialect, params DiagnosticAnalyzer[] analyzers)
        => GetVelQueryDiagnostics(compilation, dialect, [], analyzers);

    /// <summary>Generator and analyzer diagnostics with additional files (schema snapshots).</summary>
    public static IReadOnlyList<Diagnostic> GetVelQueryDiagnostics(CSharpCompilation compilation, string? dialect, (string Path, string Text)[] additionalFiles, params DiagnosticAnalyzer[] analyzers)
    {
        var (result, output) = Run(compilation, dialect, additionalFiles);
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

    private sealed class InMemoryAdditionalText(string path, string text) : AdditionalText
    {
        private readonly Microsoft.CodeAnalysis.Text.SourceText _text = Microsoft.CodeAnalysis.Text.SourceText.From(text);

        public override string Path { get; } = path;

        public override Microsoft.CodeAnalysis.Text.SourceText GetText(System.Threading.CancellationToken cancellationToken = default) => _text;
    }

    /// <summary>Supplies <c>build_property.VelQuerySqlDialect</c> as MSBuild would through CompilerVisibleProperty.</summary>
    private sealed class DialectOptionsProvider(string dialect) : AnalyzerConfigOptionsProvider
    {
        private readonly Options _global = new(dialect);

        public override AnalyzerConfigOptions GlobalOptions => _global;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Options.Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Options.Empty;

        private sealed class Options(string? dialect) : AnalyzerConfigOptions
        {
            public static readonly Options Empty = new(null);

            public override bool TryGetValue(string key, out string value)
            {
                value = dialect ?? string.Empty;
                return dialect is not null && key == "build_property.VelQuerySqlDialect";
            }
        }
    }
}
