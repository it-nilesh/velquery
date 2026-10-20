using Microsoft.CodeAnalysis;

namespace VelQuery.MongoDB.Generators.Diagnostics;

/// <summary>
/// Diagnostics reported by the MongoDB contract generator (ADR-0019). IDs are stable and live in the VQ2xxx range,
/// separate from the SQL generator's VQ1xxx: VQ2001/VQ2002 mirror VQ1001/VQ1002 (VQ-DB-006); VQ2006–VQ2008 carry
/// the NoSQL-injection guarantee.
/// </summary>
internal static class Descriptors
{
    private const string Category = "VelQuery.MongoDB.Contracts";

    public static readonly DiagnosticDescriptor UnknownPlaceholder = Create(
        "VQ2001", "Placeholder references an undeclared parameter",
        "{0} of query '{1}' references '@{2}', which is not a parameter of '{3}'; add a typed parameter or fix the spelling",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor UnusedParameter = Create(
        "VQ2002", "Method parameter is not used by the contract",
        "Parameter '{0}' of '{1}' is not referenced by query '{2}' and will not be sent; remove it or reference '@{0}'",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor UnsupportedDocumentType = Create(
        "VQ2003", "Unsupported document type",
        "Cannot generate a BSON serializer for '{0}': {1}",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor AmbiguousField = Create(
        "VQ2004", "Ambiguous member-to-field mapping",
        "Members '{1}' and '{2}' of '{0}' both map to BSON field '{3}'; rename one or use [BsonElement] to give it a distinct field name",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor MalformedJson = Create(
        "VQ2005", "Malformed contract JSON",
        "{0} of query '{1}' is not valid JSON at offset {2}: {3}",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor PlaceholderAsKey = Create(
        "VQ2006", "Placeholder used as a key or operator",
        "{0} of query '{1}': {2}. Arguments can never become field names or operators such as $where; use a constant key",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor PlaceholderNotAllowed = Create(
        "VQ2007", "Placeholder in a position that is code, an identifier or a constant",
        "{0} of query '{1}' uses '@{2}' inside {3}; arguments are not allowed there because the server would treat the value as {4}",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor UnbindableParameter = Create(
        "VQ2008", "Parameter type cannot be bound as a BSON value",
        "Parameter '{0}' of type '{1}' cannot be bound to '@{2}': {3}",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor InvalidExtendedJson = Create(
        "VQ2009", "Invalid Extended JSON value",
        "{0} of query '{1}': {2}",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor ContractTypeNotPartial = Create(
        "VQ2010", "Query contract type must be a non-generic partial class",
        "Query contract '{0}' {1}",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor ContractMethodCount = Create(
        "VQ2011", "Query contract must declare exactly one partial method",
        "Query contract '{0}' must declare exactly one partial method without a body, but declares {1}",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor InvalidMethodSignature = Create(
        "VQ2012", "Invalid query contract method signature",
        "Query method '{0}' {1}",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor UnsupportedReturnType = Create(
        "VQ2013", "Unsupported return type for the operation",
        "Query method '{0}' returns '{1}', which is not supported for {2}: {3}",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor InvalidContract = Create(
        "VQ2014", "Invalid contract argument or document shape",
        "Query contract '{0}': {1}",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor DuplicateParameter = Create(
        "VQ2015", "Duplicate parameter binding",
        "Parameters '{0}' and '{1}' both bind placeholder '@{2}' (names are compared case-insensitively)",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor DuplicateKey = Create(
        "VQ2016", "Duplicate key in a JSON object",
        "{0} of query '{1}' repeats key '{2}' in one object; MongoDB would keep only one of them",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor GeneratorFailure = Create(
        "VQ2017", "VelQuery MongoDB generator failure",
        "VelQuery could not generate MongoDB contract '{0}' ({1}: {2}); please report this issue",
        DiagnosticSeverity.Error);

    private static DiagnosticDescriptor Create(string id, string title, string message, DiagnosticSeverity severity)
        => new(id, title, message, Category, severity, isEnabledByDefault: true);
}
