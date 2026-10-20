using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MongoDB.Bson;
using MongoDB.Driver;

namespace VelQuery.MongoDB.Generators.Tests;

/// <summary>Builds in-memory compilations against the real VelQuery.MongoDB assemblies and runs the generator.</summary>
internal static class MongoGeneratorHarness
{
    public static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static readonly Lazy<ImmutableArray<MetadataReference>> s_references = new(() =>
    {
        // The test host's trusted platform assemblies are the .NET 10 reference set, available offline.
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Concat([
                typeof(QueryCardinality).Assembly.Location,
                typeof(VelQueryMongoSession).Assembly.Location,
                typeof(BsonDocument).Assembly.Location,
                typeof(IMongoClient).Assembly.Location,
            ])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.Ordinal);
        return [.. paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))];
    });

    public static CSharpCompilation Compile(params string[] sources)
        => Compile(sources.Select((s, i) => (s, $"/src/Input{i}.cs")).ToArray());

    public static CSharpCompilation Compile(params (string Source, string Path)[] sources)
        => CSharpCompilation.Create(
            "VelQuery.MongoDB.TestInput",
            sources.Select(s => CSharpSyntaxTree.ParseText(s.Source, ParseOptions, s.Path)),
            s_references.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    public static GeneratorDriver CreateDriver(bool trackSteps = false)
        => CSharpGeneratorDriver.Create(
            [new VelQueryMongoGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: trackSteps));

    public static (GeneratorRunResult Result, Compilation Output) Run(CSharpCompilation compilation)
    {
        var driver = CreateDriver().RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var result = driver.GetRunResult().Results.Single();
        if (result.Exception is not null)
        {
            throw new InvalidOperationException("Generator threw.", result.Exception);
        }

        return (result, output);
    }

    /// <summary>Generator diagnostics for a single source file wrapped with the usual usings.</summary>
    public static IReadOnlyList<Diagnostic> Diagnostics(string source) => Run(Compile(source)).Result.Diagnostics;

    /// <summary>Compiler errors and warnings in a compilation, formatted for assertion messages.</summary>
    public static string[] CompilerProblems(Compilation compilation)
        => compilation.GetDiagnostics()
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(d => d.ToString())
            .ToArray();

    /// <summary>Wraps a contract body in a namespace with the usings every test source needs.</summary>
    public static string Contract(string attribute, string method, string types = "")
        => $$"""
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using MongoDB.Bson;
            using MongoDB.Bson.Serialization.Attributes;
            using VelQuery;
            using VelQuery.MongoDB;

            namespace Sample;

            public sealed record User(int Id, string Name, int Age);

            {{types}}

            {{attribute}}
            public static partial class Contract
            {
                {{method}}
            }
            """;
}
