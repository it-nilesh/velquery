using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace VelQuery.Analyzers;

/// <summary>
/// VQ1008 (PRD §7.1, VQ-SEC-001): reports SQL text passed to a VelQuery session that is built from non-constant values
/// by string interpolation or concatenation. Values must be bound with <c>QueryParameters</c>; identifiers must come
/// from an allowlisted identifier contract.
/// </summary>
/// <remarks>
/// The check is local to the argument expression: SQL assembled in a variable first, or via <c>string.Format</c>,
/// <c>string.Concat</c> or a <c>StringBuilder</c>, is not traced.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DynamicSqlAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The VQ1008 diagnostic ID.</summary>
    public const string DiagnosticId = "VQ1008";

    internal static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "SQL text built from non-constant values",
        "SQL passed to '{0}' is built by {1} from non-constant values, which risks SQL injection; use constant SQL and bind values with QueryParameters (allowlist identifiers explicitly)",
        "VelQuery.Security",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Parameter values must always be bound, never concatenated or interpolated into SQL text (VQ-SEC-001).");

    private static readonly ImmutableHashSet<string> Methods = ImmutableHashSet.Create(
        "QueryAsync",
        "QueryFirstAsync",
        "QueryFirstOrDefaultAsync",
        "QuerySingleAsync",
        "QuerySingleOrDefaultAsync",
        "StreamAsync",
        "ExecuteAsync",
        "ExecuteScalarAsync",
        "QueryMultipleAsync",
        "Sql");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var interfaceType = start.Compilation.GetTypeByMetadataName("VelQuery.IVelQuerySession");
            var sessionType = start.Compilation.GetTypeByMetadataName("VelQuery.VelQuerySession");
            if (interfaceType is null && sessionType is null)
            {
                return;
            }

            start.RegisterOperationAction(ctx => AnalyzeInvocation(ctx, interfaceType, sessionType), OperationKind.Invocation);
        });
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, INamedTypeSymbol? interfaceType, INamedTypeSymbol? sessionType)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (!Methods.Contains(method.Name) || !IsSessionMethod(method.ContainingType, interfaceType, sessionType))
        {
            return;
        }

        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter is not { Name: "sql", Type.SpecialType: SpecialType.System_String })
            {
                continue;
            }

            var kind = DynamicKind(Unwrap(argument.Value));
            if (kind is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, argument.Value.Syntax.GetLocation(), method.Name, kind));
            }
        }
    }

    private static bool IsSessionMethod(INamedTypeSymbol type, INamedTypeSymbol? interfaceType, INamedTypeSymbol? sessionType)
        => SymbolEqualityComparer.Default.Equals(type, interfaceType) || SymbolEqualityComparer.Default.Equals(type, sessionType);

    /// <summary>Returns "interpolation" or "concatenation" when the SQL depends on non-constant values, else null.</summary>
    private static string? DynamicKind(IOperation operation)
    {
        if (operation.ConstantValue.HasValue)
        {
            return null; // Constant interpolations/concatenations are folded by the compiler and are safe.
        }

        switch (operation)
        {
            case IInterpolatedStringOperation interpolated:
                foreach (var part in interpolated.Parts)
                {
                    if (part is IInterpolationOperation hole && !Unwrap(hole.Expression).ConstantValue.HasValue)
                    {
                        return "interpolation";
                    }
                }

                return null;
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } binary when binary.Type?.SpecialType == SpecialType.System_String:
                return IsConstantTree(binary) ? null : "concatenation";
            default:
                return null;
        }
    }

    private static bool IsConstantTree(IOperation operation)
    {
        operation = Unwrap(operation);
        if (operation.ConstantValue.HasValue)
        {
            return true;
        }

        return operation switch
        {
            IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } binary => IsConstantTree(binary.LeftOperand) && IsConstantTree(binary.RightOperand),
            IInterpolatedStringOperation interpolated => DynamicKind(interpolated) is null,
            _ => false,
        };
    }

    private static IOperation Unwrap(IOperation operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    continue;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    continue;
                default:
                    return operation;
            }
        }
    }
}
