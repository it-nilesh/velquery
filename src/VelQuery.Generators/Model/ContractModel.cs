using VelQuery.Generators.Infrastructure;

namespace VelQuery.Generators.Model;

// Pipeline models hold only strings, primitives and equatable arrays: never ISymbol, SyntaxNode or Compilation, so
// the incremental driver can compare them by value and skip regeneration on unrelated edits (PRD §11.1).

/// <summary>Session method invoked by the generated implementation.</summary>
internal enum QueryOperation
{
    Query,
    Stream,
    First,
    FirstOrDefault,
    Single,
    SingleOrDefault,
    Scalar,
    Execute,
}

/// <summary>Output of the per-contract transform: an optional model plus diagnostics and the schema-check input.</summary>
internal sealed record ContractResult(ContractModel? Model, RowPlan? RowPlan, EquatableArray<DiagnosticInfo> Diagnostics, Schema.SchemaCheckInput? SchemaCheck);

/// <summary>One <see cref="ContractResult"/> per SQL dialect, indexed by <c>SqlDialect</c>.</summary>
internal sealed record ContractVariants(EquatableArray<ContractResult> Results);

/// <summary>Everything needed to emit one contract's source file.</summary>
internal sealed record ContractModel(
    string HintName,
    string? Namespace,
    EquatableArray<TypeDeclarationModel> ContainingTypes,
    TypeDeclarationModel ContractType,
    string QueryId,
    string Sql,
    string CommandType,
    int TimeoutSeconds,
    QueryOperation Operation,
    string? RowType,
    string? RowFactory,
    MethodModel Method,
    EquatableArray<BindingModel> Bindings);

/// <summary>A (partial) type declaration to re-open in generated code.</summary>
internal sealed record TypeDeclarationModel(string Keyword, string Name, bool IsStatic);

/// <summary>The contract's partial method signature.</summary>
internal sealed record MethodModel(
    string Accessibility,
    bool IsStatic,
    string Name,
    string ReturnType,
    EquatableArray<MethodParameterModel> Parameters,
    string SessionParameter,
    string? CancellationTokenParameter);

/// <summary>A parameter of the contract method, repeated verbatim in the implementation.</summary>
internal sealed record MethodParameterModel(string Name, string Type);

/// <summary>A method parameter bound to a SQL placeholder.</summary>
/// <param name="FieldName">Identifier of the field on the generated args struct.</param>
/// <param name="ParameterName">The method parameter (escaped identifier).</param>
/// <param name="SqlName">The SQL parameter name (without prefix), as spelled in the SQL.</param>
/// <param name="Type">Fully qualified parameter type.</param>
/// <param name="DbType">A <c>System.Data.DbType</c> member name, or null.</param>
/// <param name="EnumUnderlyingType">Underlying type for enum parameters (bound as their integral value), or null.</param>
/// <param name="Size">Explicit size from <c>[VelQueryParameter]</c>, or null.</param>
/// <param name="Precision">Explicit precision from <c>[VelQueryParameter]</c>, or null.</param>
/// <param name="Scale">Explicit scale from <c>[VelQueryParameter]</c>, or null.</param>
/// <param name="IsNullableValueType">Whether <see cref="Type"/> is <c>Nullable&lt;T&gt;</c>.</param>
internal sealed record BindingModel(
    string FieldName,
    string ParameterName,
    string SqlName,
    string Type,
    string? DbType,
    int? Size,
    int? Precision,
    int? Scale,
    string? EnumUnderlyingType,
    bool IsNullableValueType);

/// <summary>Compile-time mapping plan for a non-scalar row type.</summary>
internal sealed record RowPlan(
    string Type,
    string FactoryName,
    string HintName,
    EquatableArray<RowMemberModel> Members);

/// <summary>How a member receives its value.</summary>
internal enum MemberKind
{
    /// <summary>Constructor argument; column required.</summary>
    Constructor,

    /// <summary>Assigned in the object initializer (init-only or <c>required</c>); column required.</summary>
    Initializer,

    /// <summary>Settable property assigned only when the column is present.</summary>
    Optional,
}

/// <summary>One mapped constructor parameter or property.</summary>
/// <param name="Name">Member or parameter identifier (escaped).</param>
/// <param name="Column">Column name matched case-insensitively.</param>
/// <param name="Type">Declared member type, fully qualified, including nullable annotation.</param>
/// <param name="ValueType">Non-nullable target type (underlying type of <c>Nullable&lt;T&gt;</c>).</param>
/// <param name="Getter">Typed reader call, for example <c>GetInt64</c> or <c>GetFieldValue&lt;global::System.DateOnly&gt;</c>.</param>
/// <param name="FastTypes">Provider field types for which <see cref="Getter"/> is used directly.</param>
/// <param name="EnumCast">Enum type to cast the getter result to, or null.</param>
/// <param name="IsNullable">Whether the member accepts NULL (nullable value type, or reference type not annotated non-null).</param>
/// <param name="Kind">How the member is assigned.</param>
internal sealed record RowMemberModel(
    string Name,
    string Column,
    string Type,
    string ValueType,
    string Getter,
    EquatableArray<string> FastTypes,
    string? EnumCast,
    bool IsNullable,
    MemberKind Kind);
