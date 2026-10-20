using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using VelQuery.Generators.Infrastructure;
using VelQuery.MongoDB.Generators.Analysis;
using VelQuery.MongoDB.Generators.Emit;
using VelQuery.MongoDB.Generators.Model;

namespace VelQuery.MongoDB.Generators;

/// <summary>
/// Incremental generator for VelQuery.MongoDB contracts (<c>[VelQueryMongoFind]</c>, <c>[VelQueryMongoCount]</c>,
/// <c>[VelQueryMongoAggregate]</c>, <c>[VelQueryMongoUpdate]</c>, <c>[VelQueryMongoDelete]</c>,
/// <c>[VelQueryMongoInsert]</c>; ADR-0017). For each contract it checks the JSON at build time and emits a statically
/// cached descriptor, typed binders that build the BSON directly and the method body; for each distinct document type
/// it emits a reflection-free BSON serializer that reaches the driver per operation, never through a registry. Output
/// contains no timestamps and is ordered deterministically.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class VelQueryMongoGenerator : IIncrementalGenerator
{
    /// <summary>Version stamped into <c>[GeneratedCode]</c>; changes only with the generator's output format.</summary>
    public const string Version = "0.1.0";

    /// <summary>Tracking names of the pipeline steps, for incrementality tests.</summary>
    public static class TrackingNames
    {
        /// <summary>Per-contract parse results.</summary>
        public const string Contracts = "VelQuery.Mongo.Contracts";

        /// <summary>Per-contract source models.</summary>
        public const string Models = "VelQuery.Mongo.Models";

        /// <summary>Collected, de-duplicated document plans.</summary>
        public const string Documents = "VelQuery.Mongo.Documents";
    }

    private static readonly (string Attribute, ContractKind Kind)[] Attributes =
    [
        ("VelQuery.MongoDB.VelQueryMongoFindAttribute", ContractKind.Find),
        ("VelQuery.MongoDB.VelQueryMongoCountAttribute", ContractKind.Count),
        ("VelQuery.MongoDB.VelQueryMongoAggregateAttribute", ContractKind.Aggregate),
        ("VelQuery.MongoDB.VelQueryMongoUpdateAttribute", ContractKind.Update),
        ("VelQuery.MongoDB.VelQueryMongoDeleteAttribute", ContractKind.Delete),
        ("VelQuery.MongoDB.VelQueryMongoInsertAttribute", ContractKind.Insert),
    ];

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<EquatableArray<DocumentPlan>>>? allPlans = null;
        foreach (var (attribute, kind) in Attributes)
        {
            var contracts = context.SyntaxProvider
                .ForAttributeWithMetadataName(attribute, static (node, _) => node is ClassDeclarationSyntax, (ctx, ct) => ContractParser.Parse(ctx, kind, ct))
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

            var models = contracts
                .Select(static (c, _) => c.Model)
                .Where(static m => m is not null)
                .WithTrackingName(TrackingNames.Models);
            context.RegisterSourceOutput(models, static (spc, model) =>
                spc.AddSource(model!.HintName, SourceText.From(ContractEmitter.Emit(model), Encoding.UTF8)));

            var plans = contracts.Select(static (c, _) => c.Documents).Collect();
            allPlans = allPlans is null
                ? plans
                : allPlans.Value.Combine(plans).Select(static (pair, _) => pair.Left.AddRange(pair.Right));
        }

        var documents = allPlans!.Value
            .Select(static (plans, _) => Distinct(plans))
            .WithTrackingName(TrackingNames.Documents);
        context.RegisterSourceOutput(documents, static (spc, plans) =>
        {
            foreach (var plan in plans)
            {
                spc.AddSource(plan.HintName, SourceText.From(SerializerEmitter.Emit(plan), Encoding.UTF8));
            }
        });
    }

    private static EquatableArray<DocumentPlan> Distinct(ImmutableArray<EquatableArray<DocumentPlan>> plans)
    {
        // Ordinal ordering keeps output stable regardless of file or declaration order (VQ-FR-032).
        var unique = new SortedDictionary<string, DocumentPlan>(StringComparer.Ordinal);
        foreach (var plan in plans.SelectMany(p => p))
        {
            if (!unique.ContainsKey(plan.Type))
            {
                unique.Add(plan.Type, plan);
            }
        }

        return unique.Values.ToEquatableArray();
    }
}
