using VelQuery.Generators.Infrastructure;

namespace VelQuery.MongoDB.Generators.Model;

/// <summary>The kind of contract attribute.</summary>
internal enum ContractKind
{
    Find,
    Count,
    Aggregate,
    Update,
    Delete,
    Insert,
}

/// <summary>The session method a contract calls.</summary>
internal enum Operation
{
    List,
    Stream,
    First,
    FirstOrDefault,
    Single,
    SingleOrDefault,
    Count,
    Update,
    Delete,
    InsertOne,
    InsertMany,
}

/// <summary>How a value is represented: a scalar, an embedded generated document, or an array.</summary>
internal enum ShapeKind
{
    Scalar,
    Document,
    List,
}

/// <summary>
/// The compile-time shape of a member, collection element or bound parameter. <see cref="Scalar"/> names the typed
/// helper suffix (<c>Int32</c>, <c>String</c>, …) in <c>MongoBsonReading</c>/<c>MongoBsonWriting</c>/<c>MongoBsonValues</c>;
/// <see cref="DeclaredType"/> keeps nullable annotations, <see cref="BareType"/> is usable in <c>typeof</c>.
/// </summary>
internal sealed record ValueShape(
    ShapeKind Kind,
    string Scalar,
    string DeclaredType,
    string BareType,
    bool Nullable,
    string? EnumType,
    string? EnumUnderlying,
    string? Serializer,
    ValueShape? Element,
    bool IsArray);

/// <summary>How a mapped member is assigned when a document is read.</summary>
internal enum FieldRole
{
    /// <summary>Constructor parameter.</summary>
    Constructor,

    /// <summary><c>required</c> or <c>init</c> property, assigned in the object initializer.</summary>
    Initializer,

    /// <summary>Settable property, assigned only when the field is present.</summary>
    Optional,
}

/// <summary>A mapped member of a document type.</summary>
internal sealed record FieldPlan(
    int ConstructorIndex,
    string Member,
    string Property,
    string Field,
    ValueShape Shape,
    FieldRole Role,
    bool IgnoreIfNull,
    bool OmitEmptyObjectId);

/// <summary>A document type for which a serializer is generated.</summary>
internal sealed record DocumentPlan(
    string Type,
    string SerializerName,
    string HintName,
    EquatableArray<FieldPlan> Fields);

/// <summary>A method parameter as declared.</summary>
internal sealed record MethodParameterModel(string Name, string Type);

/// <summary>The partial method to implement.</summary>
internal sealed record MethodModel(
    string Accessibility,
    bool IsStatic,
    string Name,
    string ReturnType,
    EquatableArray<MethodParameterModel> Parameters,
    string SessionName,
    string? TokenName);

/// <summary>A containing or contract type declaration.</summary>
internal sealed record TypeDeclarationModel(string Keyword, string Name, bool IsStatic);

/// <summary>A field of the generated arguments struct.</summary>
internal sealed record ArgumentModel(string Field, string Parameter, string Type);

/// <summary>Everything needed to emit one contract; equatable so unchanged contracts stay cached.</summary>
internal sealed record ContractModel(
    string HintName,
    string? Namespace,
    EquatableArray<TypeDeclarationModel> ContainingTypes,
    TypeDeclarationModel ContractType,
    ContractKind Kind,
    Operation Operation,
    string QueryId,
    string Collection,
    string? DocumentType,
    string? Serializer,
    EquatableArray<ArgumentModel> Arguments,
    string? FilterExpression,
    string? UpdateExpression,
    string? SortExpression,
    string? ProjectionExpression,
    EquatableArray<string> PipelineStages,
    int Limit,
    int Skip,
    bool Many,
    bool Upsert,
    bool AllowDiskUse,
    int TimeoutMilliseconds,
    string? DocumentParameter,
    MethodModel Method);

/// <summary>Per-contract output of the parse step.</summary>
internal sealed record ContractResult(ContractModel? Model, EquatableArray<DocumentPlan> Documents, EquatableArray<DiagnosticInfo> Diagnostics);
