using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using VelQuery.Testing;
using Xunit;

namespace VelQuery.Generators.GoldenTests;

/// <summary>Determinism (VQ-FR-032) and incrementality (PRD §11.1) of the generator pipeline.</summary>
public sealed class DeterminismTests
{
    private const string Contracts = GoldenCases.RecordRow;

    private const string Unrelated = """
        namespace Sample.Other;

        public static class Helpers
        {
            public static int Twice(int value) => value * 2;
        }
        """;

    [Fact]
    public void Running_twice_produces_identical_output()
    {
        var first = Generate(GeneratorHarness.Compile(GoldenCases.ClassRow, GoldenCases.Stream.Replace("Sample.Users", "Sample.Streams", StringComparison.Ordinal)));
        var second = Generate(GeneratorHarness.Compile(GoldenCases.ClassRow, GoldenCases.Stream.Replace("Sample.Users", "Sample.Streams", StringComparison.Ordinal)));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Input_file_order_does_not_change_output()
    {
        var stream = GoldenCases.Stream.Replace("Sample.Users", "Sample.Streams", StringComparison.Ordinal);
        var forward = Generate(GeneratorHarness.Compile((GoldenCases.ClassRow, "/src/A.cs"), (stream, "/src/B.cs"), (GoldenCases.RecordRow, "/src/C.cs")));
        var reverse = Generate(GeneratorHarness.Compile((GoldenCases.RecordRow, "/src/C.cs"), (stream, "/src/B.cs"), (GoldenCases.ClassRow, "/src/A.cs")));

        Assert.Equal(forward, reverse);
    }

    [Fact]
    public void Unrelated_edit_keeps_all_steps_cached()
    {
        var compilation = GeneratorHarness.Compile((Contracts, "/src/Contracts.cs"), (Unrelated, "/src/Helpers.cs"));
        var driver = GeneratorHarness.CreateDriver(trackSteps: true).RunGenerators(compilation);

        // Edit the unrelated file and add a new one.
        var helpers = compilation.SyntaxTrees.Single(t => t.FilePath == "/src/Helpers.cs");
        var edited = compilation
            .ReplaceSyntaxTree(helpers, CSharpSyntaxTree.ParseText(Unrelated.Replace("value * 2", "value + value", StringComparison.Ordinal), GeneratorHarness.ParseOptions, "/src/Helpers.cs"))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Sample.Other; public sealed class Added { }", GeneratorHarness.ParseOptions, "/src/Added.cs"));
        var result = driver.RunGenerators(edited).GetRunResult().Results.Single();

        foreach (var name in new[] { VelQuerySqlGenerator.TrackingNames.Contracts, VelQuerySqlGenerator.TrackingNames.Models, VelQuerySqlGenerator.TrackingNames.RowPlans })
        {
            var steps = result.TrackedSteps[name];
            Assert.All(steps.SelectMany(s => s.Outputs), o => Assert.True(
                o.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, $"{name}: {o.Reason}"));
        }

        Assert.All(result.TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs), o => Assert.True(
            o.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, $"output: {o.Reason}"));
    }

    [Fact]
    public void Edit_above_contract_in_same_file_does_not_regenerate_source()
    {
        var compilation = GeneratorHarness.Compile((Contracts, "/src/Contracts.cs"));
        var driver = GeneratorHarness.CreateDriver(trackSteps: true).RunGenerators(compilation);

        // Shifting the contract down changes diagnostic locations only; the source model must stay equal.
        var tree = compilation.SyntaxTrees.Single();
        var edited = compilation.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText("// a comment\n" + Contracts, GeneratorHarness.ParseOptions, "/src/Contracts.cs"));
        var result = driver.RunGenerators(edited).GetRunResult().Results.Single();

        Assert.All(result.TrackedSteps[VelQuerySqlGenerator.TrackingNames.Models].SelectMany(s => s.Outputs), o => Assert.True(
            o.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, $"models: {o.Reason}"));
    }

    [Fact]
    public void Relevant_edit_regenerates_contract()
    {
        var compilation = GeneratorHarness.Compile((Contracts, "/src/Contracts.cs"));
        var driver = GeneratorHarness.CreateDriver(trackSteps: true).RunGenerators(compilation);

        var tree = compilation.SyntaxTrees.Single();
        var edited = compilation.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(
            Contracts.Replace("FROM dbo.Users", "FROM dbo.AllUsers", StringComparison.Ordinal), GeneratorHarness.ParseOptions, "/src/Contracts.cs"));
        var result = driver.RunGenerators(edited).GetRunResult().Results.Single();

        Assert.Contains(result.TrackedSteps[VelQuerySqlGenerator.TrackingNames.Models].SelectMany(s => s.Outputs),
            o => o.Reason == IncrementalStepRunReason.Modified);
        Assert.Contains("dbo.AllUsers", result.GeneratedSources.Single(s => s.HintName == "Sample.Users.UsersById.g.cs").SourceText.ToString(), StringComparison.Ordinal);
    }

    private static string[] Generate(CSharpCompilation compilation)
    {
        var (result, _) = GeneratorHarness.Run(compilation);
        return result.GeneratedSources.Select(s => s.HintName + "\n" + s.SourceText).Order(StringComparer.Ordinal).ToArray();
    }
}
