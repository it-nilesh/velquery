using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using VelQuery.Generators.Analysis;
using VelQuery.Generators.Emit;
using VelQuery.Generators.Infrastructure;
using VelQuery.Generators.Model;
using VelQuery.Generators.Schema;

namespace VelQuery.Generators;

/// <summary>
/// Incremental generator for <c>[VelQuerySql]</c> query contracts (VQ-FR-031, VQ-FR-032, PRD §11.1). For each
/// contract it emits a static <c>QueryDescriptor</c>, a parameter binder with compile-time <c>ParameterSpec</c>s and
/// the partial method implementation; for each distinct row type it emits an <c>IRowMapperFactory&lt;T&gt;</c> and
/// registers it with <c>RowMappers&lt;T&gt;</c> from a module initializer so runtime SQL for that type is
/// reflection-free too. Output contains no timestamps and is ordered deterministically.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class VelQuerySqlGenerator : IIncrementalGenerator
{
    /// <summary>Version stamped into <c>[GeneratedCode]</c>; changes only with the generator's output format.</summary>
    public const string Version = "0.1.0";

    /// <summary>Tracking names of the pipeline steps, for incrementality tests.</summary>
    public static class TrackingNames
    {
        /// <summary>Per-contract parse results for every dialect.</summary>
        public const string ContractVariants = "VelQuery.ContractVariants";

        /// <summary>Per-contract parse results for the project's dialect.</summary>
        public const string Contracts = "VelQuery.Contracts";

        /// <summary>Per-contract source models.</summary>
        public const string Models = "VelQuery.Models";

        /// <summary>Collected, de-duplicated row mapping plans.</summary>
        public const string RowPlans = "VelQuery.RowPlans";

        /// <summary>The parsed schema snapshot (<c>*.vqschema.json</c> additional file).</summary>
        public const string SchemaSnapshot = "VelQuery.SchemaSnapshot";

        /// <summary>Per-contract schema snapshot diagnostics.</summary>
        public const string SchemaChecks = "VelQuery.SchemaChecks";
    }

    private const string AttributeName = "VelQuery.VelQuerySqlAttribute";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // The SQL dialect comes from the VelQuerySqlDialect MSBuild property (made compiler-visible by the package's
        // build props); provider packages set a default. Empty keeps the T-SQL rules.
        var dialect = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
            options.GlobalOptions.TryGetValue("build_property.VelQuerySqlDialect", out var value) ? value : null);

        var contracts = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeName,
                static (node, _) => node is ClassDeclarationSyntax,
                ContractParser.Parse)
            .WithTrackingName(TrackingNames.ContractVariants)
            .Combine(dialect)
            .Select(static (pair, _) => ContractParser.Select(pair.Left, pair.Right))
            .WithTrackingName(TrackingNames.Contracts);

        // Diagnostics carry locations, which shift on unrelated edits above the contract; keep them out of the
        // source pipeline so such edits do not regenerate code.
        context.RegisterSourceOutput(contracts.Select(static (c, _) => c.Diagnostics), static (spc, diagnostics) =>
        {
            foreach (var diagnostic in diagnostics)
            {
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
            }
        });

        // Schema snapshot checks (ADR-0006): diagnostics only, so neither the snapshot nor the checks affect generated
        // code. The snapshot is an additional file; the build never reads a database (VQ-FR-035).
        var snapshot = context.AdditionalTextsProvider
            .Where(static file => SchemaSnapshotReader.IsSnapshotPath(file.Path))
            .Select(static (file, cancellationToken) => (file.Path, file.GetText(cancellationToken)?.ToString()))
            .Collect()
            .Select(static (files, _) => SchemaSnapshotReader.Read(files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList()))
            .WithTrackingName(TrackingNames.SchemaSnapshot);
        context.RegisterSourceOutput(snapshot.Select(static (s, _) => s.Problems), ReportAll);

        var catalog = snapshot.Select(static (s, _) => new SchemaCatalogSet(s.Snapshots));
        var schemaChecks = contracts
            .Select(static (c, _) => c.SchemaCheck)
            .Where(static c => c is not null)
            .Combine(catalog)
            .Select(static (pair, _) => SchemaChecker.Check(pair.Left!, pair.Right))
            .WithTrackingName(TrackingNames.SchemaChecks);
        context.RegisterSourceOutput(schemaChecks, ReportAll);

        var models = contracts
            .Select(static (c, _) => c.Model)
            .Where(static m => m is not null)
            .WithTrackingName(TrackingNames.Models);
        context.RegisterSourceOutput(models, static (spc, model) =>
            spc.AddSource(model!.HintName, SourceText.From(ContractEmitter.Emit(model), System.Text.Encoding.UTF8)));

        var rowPlans = contracts
            .Select(static (c, _) => c.Model is null ? null : c.RowPlan)
            .Where(static p => p is not null)
            .Collect()
            .Select(static (plans, _) => Distinct(plans!))
            .WithTrackingName(TrackingNames.RowPlans);
        context.RegisterSourceOutput(rowPlans, static (spc, plans) =>
        {
            if (plans.Count == 0)
            {
                return;
            }

            foreach (var plan in plans)
            {
                spc.AddSource(plan.HintName, SourceText.From(RowMapperEmitter.Emit(plan), System.Text.Encoding.UTF8));
            }

            spc.AddSource("VelQuery.Generated.RowMapperRegistrations.g.cs", SourceText.From(EmitRegistrations(plans), System.Text.Encoding.UTF8));
        });
    }

    private static void ReportAll(SourceProductionContext context, EquatableArray<DiagnosticInfo> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            context.ReportDiagnostic(diagnostic.ToDiagnostic());
        }
    }

    private static EquatableArray<RowPlan> Distinct(ImmutableArray<RowPlan?> plans)
    {
        // Ordinal ordering keeps output stable regardless of file or declaration order (VQ-FR-032).
        var unique = new SortedDictionary<string, RowPlan>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            if (plan is not null && !unique.ContainsKey(plan.Type))
            {
                unique.Add(plan.Type, plan);
            }
        }

        return unique.Values.ToEquatableArray();
    }

    private static string EmitRegistrations(EquatableArray<RowPlan> plans)
    {
        var writer = CodeWriter.WithHeader();
        writer.Open("namespace VelQuery.Generated");
        writer.Line("/// <summary>Registers generated row mappers so runtime SQL for these types is reflection-free.</summary>");
        writer.Line(ContractEmitter.GeneratedCodeAttribute);
        writer.Open("internal static class RowMapperRegistrations");
        writer.Line("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        writer.Open("internal static void Register()");
        foreach (var plan in plans)
        {
            writer.Line($"global::VelQuery.Mapping.RowMappers<{plan.Type}>.Register({plan.FactoryName}.Instance);");
        }

        writer.Close();
        writer.Close();
        writer.Close();
        return writer.ToString();
    }
}
