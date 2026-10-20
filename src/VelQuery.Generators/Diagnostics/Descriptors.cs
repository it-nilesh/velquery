using Microsoft.CodeAnalysis;

namespace VelQuery.Generators.Diagnostics;

/// <summary>
/// Diagnostics reported by the contract generator (PRD §7.1). IDs are stable; VQ1012 and above are VelQuery-specific
/// additions to the PRD table. VQ1008 is reported by VelQuery.Analyzers.
/// </summary>
internal static class Descriptors
{
    private const string Category = "VelQuery.Contracts";

    public static readonly DiagnosticDescriptor UnknownSqlParameter = Create(
        "VQ1001", "SQL references an undeclared parameter",
        "SQL of query '{0}' references parameter '@{1}', which is not a parameter of '{2}'; add a typed parameter or fix the spelling",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor UnusedParameter = Create(
        "VQ1002", "Method parameter is not used by the SQL",
        "Parameter '{0}' of '{1}' is not referenced by the SQL of query '{2}' and will not be bound; remove it or reference '@{3}'",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor UnsupportedRowType = Create(
        "VQ1003", "Unsupported result type",
        "Cannot generate a row mapper for '{0}': {1}",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor AmbiguousMapping = Create(
        "VQ1004", "Ambiguous column-to-member mapping",
        "Members '{1}' and '{2}' of '{0}' both map to column '{3}'; rename one or use [Column] to give it a distinct column",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor ContractTypeNotPartial = Create(
        "VQ1012", "Query contract type must be a non-generic partial class",
        "Query contract '{0}' {1}",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor ContractMethodCount = Create(
        "VQ1013", "Query contract must declare exactly one partial method",
        "Query contract '{0}' must declare exactly one partial method without a body, but declares {1}",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor InvalidMethodSignature = Create(
        "VQ1014", "Invalid query contract method signature",
        "Query method '{0}' {1}",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor UnsupportedReturnType = Create(
        "VQ1015", "Unsupported return type for the query cardinality",
        "Query method '{0}' returns '{1}', which is not supported with Cardinality = {2}: {3}",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor AmbiguousOperation = Create(
        "VQ1016", "Query operation is ambiguous",
        "Query method '{0}' returning '{1}' is ambiguous: {2}; set Cardinality explicitly on [VelQuerySql]",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor InvalidAttributeArgument = Create(
        "VQ1017", "Invalid [VelQuerySql] argument",
        "Query contract '{0}': {1}",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor UnsupportedParameterType = Create(
        "VQ1018", "Parameter type cannot be bound",
        "Parameter '{0}' of type '{1}' has no inferable DbType; use a supported type or specify [VelQueryParameter(DbType = ...)]",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor UnterminatedSql = Create(
        "VQ1019", "SQL text could not be fully scanned",
        "SQL of query '{0}' contains an unterminated {1}; parameters after it were not checked",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor DuplicateParameter = Create(
        "VQ1020", "Duplicate parameter binding",
        "Parameters '{0}' and '{1}' both bind SQL parameter '@{2}'",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor GeneratorFailure = Create(
        "VQ1021", "VelQuery generator failure",
        "VelQuery could not generate query contract '{0}' ({1}: {2}); please report this issue",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor ForeignPlaceholder = Create(
        "VQ1022", "Placeholder style is not bound by VelQuery",
        "SQL of query '{0}' uses '{1}', a placeholder style VelQuery does not bind ({2} dialect); use '@name' placeholders",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor InconsistentPlaceholderCase = Create(
        "VQ1023", "Placeholder spelled with different casing",
        "SQL of query '{0}' spells parameter '@{1}' also as '@{2}'; use one spelling (some drivers, such as SQLite, bind names case-sensitively)",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor UnknownDialect = Create(
        "VQ1024", "Unknown SQL dialect",
        "Unknown SQL dialect '{0}'; expected sqlserver, postgresql, mysql, sqlite or generic (generic rules are used)",
        DiagnosticSeverity.Warning);

    private static DiagnosticDescriptor Create(string id, string title, string message, DiagnosticSeverity severity)
        => new(id, title, message, Category, severity, isEnabledByDefault: true);
}
