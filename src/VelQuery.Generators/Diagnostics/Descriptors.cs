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

    // ---- Schema snapshot checks (ADR-0006, VQ-FR-036, VQ-FR-037). Only reported when the project has a snapshot. ----

    public static readonly DiagnosticDescriptor NotInSnapshot = Create(
        "VQ1005", "SQL references a table or column that is not in the schema snapshot",
        "Query '{0}': {1} does not exist in the schema snapshot{2}",
        DiagnosticSeverity.Error,
        "Fix the name, or refresh the snapshot with 'velquery schema snapshot' if the database changed.");

    public static readonly DiagnosticDescriptor NotSchemaChecked = Create(
        "VQ1011", "Query was not fully checked against the schema snapshot",
        "Query '{0}' was not fully checked against the schema snapshot: {1}",
        DiagnosticSeverity.Info,
        "Static checks cover a subset of SQL. Parts outside it are not validated; cover them with integration tests.");

    public static readonly DiagnosticDescriptor MissingResultColumn = Create(
        "VQ1025", "Query does not return a column the result type requires",
        "Query '{0}' returns no column named '{1}', which '{2}' requires{3}",
        DiagnosticSeverity.Error,
        "Constructor parameters and init/required members need a column; the mapper throws when it is missing.");

    public static readonly DiagnosticDescriptor NullableToNonNullable = Create(
        "VQ1026", "Nullable column read into a non-nullable member",
        "Query '{0}': column '{1}' can be NULL, but {2} does not accept NULL; strict mapping throws when a NULL is read",
        DiagnosticSeverity.Warning,
        "Make the member nullable, or make the SQL return a non-NULL value (for example with COALESCE).");

    public static readonly DiagnosticDescriptor IncompatibleColumnType = Create(
        "VQ1027", "Column type cannot be converted to the member type",
        "Query '{0}': column '{1}' has database type '{2}', which VelQuery cannot convert to '{3}' ({4})",
        DiagnosticSeverity.Warning,
        "VelQuery converts losslessly only; change the member type or CAST the column in SQL.");

    public static readonly DiagnosticDescriptor DuplicateResultColumn = Create(
        "VQ1028", "Query returns the same column name more than once",
        "Query '{0}' returns more than one column named '{1}', so '{2}' cannot be mapped; alias one of them",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor InsertMissesRequiredColumn = Create(
        "VQ1029", "INSERT does not set a required column",
        "Query '{0}': INSERT into '{1}' does not set '{2}', which is NOT NULL and has no default",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor WritesGeneratedColumn = Create(
        "VQ1030", "SQL writes a column the database generates",
        "Query '{0}': column '{1}' of '{2}' is generated by the database and cannot be written",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor PossiblyUnknownColumn = Create(
        "VQ1032", "Name is neither a known column nor a known keyword",
        "Query '{0}': '{1}' is not a column of {2}; if it is meant to be a column, it does not exist in the schema snapshot",
        DiagnosticSeverity.Info,
        "Reported instead of VQ1005 when an unqualified, unquoted name could also be a keyword or function the checker does not know (for example UTC_TIMESTAMP). Qualify it (t.col) to get a definite VQ1005.");

    public static readonly DiagnosticDescriptor UnknownSchemaName = Create(
        "VQ1033", "Contract names a schema snapshot the project does not have",
        "Query '{0}' names schema snapshot '{1}', but the project has none by that name (available: {2})",
        DiagnosticSeverity.Warning,
        "A snapshot's name is its databaseLogicalName, or its file name without .vqschema.json.");

    public static readonly DiagnosticDescriptor SchemaSnapshotProblem = Create(
        "VQ1031", "Schema snapshot cannot be used",
        "Schema snapshot '{0}' is not used: {1}",
        DiagnosticSeverity.Warning,
        "Regenerate the file with 'velquery schema snapshot'; queries are not schema-checked until it is fixed.");

    private static DiagnosticDescriptor Create(string id, string title, string message, DiagnosticSeverity severity, string description)
        => new(id, title, message, Category, severity, isEnabledByDefault: true, description: description);

    private static DiagnosticDescriptor Create(string id, string title, string message, DiagnosticSeverity severity)
        => new(id, title, message, Category, severity, isEnabledByDefault: true);
}
