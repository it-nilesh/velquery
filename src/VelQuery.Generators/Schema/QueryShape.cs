using VelQuery.Generators.Infrastructure;
using VelQuery.Generators.Sql;

namespace VelQuery.Generators.Schema;

// The shape of a contract's SQL as far as the schema checks need it (ADR-0006): which tables each query block reads,
// which names it references, and which columns it returns. Built per contract without the snapshot (so the result is
// cached with the contract) and resolved against the snapshot later. Like every pipeline model it holds only values.

/// <summary>An identifier as written: its text (unescaped when quoted) and whether it was quoted.</summary>
internal sealed record SqlName(string Text, bool Quoted)
{
    public override string ToString() => Text;
}

/// <summary>A table or view named in FROM, JOIN, INSERT, UPDATE or DELETE.</summary>
/// <remarks>
/// <para><c>Database</c>: Database or catalog part of a three-part name; such names are never checked.</para>
/// </remarks>
internal sealed record TableReference(SqlName? Database, SqlName? Schema, SqlName Name, LocationInfo? Location)
{
    public string Display => Schema is null ? Name.Text : Schema.Text + "." + Name.Text;
}

internal enum SourceKind
{
    /// <summary>A table or view; <see cref="QuerySource.Table"/> indexes <see cref="QueryShape.Tables"/>.</summary>
    Table,

    /// <summary>A derived table or CTE; its columns are the outputs of <see cref="QuerySource.Scope"/>.</summary>
    Derived,

    /// <summary>A source whose columns cannot be known (function, temp table, table variable, VALUES, recursive CTE).</summary>
    Unknown,
}

/// <summary>A row source visible in a query block.</summary>
/// <remarks>
/// <para><c>Alias</c>: Name the block uses for the source: its alias, or the table name when there is none.</para>
/// <para><c>Renames</c>: Column alias list (<c>AS d(a, b)</c>), renaming the source's columns by position.</para>
/// <para><c>Nullable</c>: Whether the source is on the optional side of an outer join, so all its columns may be NULL.</para>
/// <para><c>Pseudo</c>: A pseudo-table such as <c>inserted</c> or <c>excluded</c>: resolvable, never expanded by <c>*</c>.</para>
/// </remarks>
internal sealed record QuerySource(SqlName? Alias, SourceKind Kind, int Table, int Scope, EquatableArray<SqlName> Renames, bool Nullable, bool Pseudo);

/// <summary>A column reference: <c>col</c>, <c>q.col</c> or <c>schema.table.col</c>.</summary>
/// <remarks>
/// <para><c>Target</c>: For written columns (INSERT list, UPDATE SET), the only source the column may come from; else -1.</para>
/// <para><c>Write</c>: Whether the statement writes this column.</para>
/// </remarks>
internal sealed record ColumnReference(EquatableArray<SqlName> Parts, LocationInfo? Location, int Target, bool Write)
{
    public SqlName Column => Parts[Parts.Count - 1];

    public string Display => string.Join(".", System.Linq.Enumerable.Select(Parts, p => p.Text));
}

internal enum OutputKind
{
    /// <summary><c>*</c>: every column of every non-pseudo source.</summary>
    Star,

    /// <summary><c>q.*</c>: every column of one source.</summary>
    QualifiedStar,

    /// <summary>A plain column reference, possibly aliased.</summary>
    Column,

    /// <summary>Any other expression; its name is the alias, or unknown.</summary>
    Expression,
}

/// <summary>One item of a select list, OUTPUT or RETURNING clause.</summary>
internal sealed record QueryOutput(OutputKind Kind, SqlName? Name, ColumnReference? Reference, EquatableArray<SqlName> Qualifier);

/// <summary>One query block (SELECT, INSERT, UPDATE or DELETE) or a set operation over blocks.</summary>
/// <remarks>
/// <para><c>Parent</c>: Enclosing block for correlated names, or -1.</para>
/// <para><c>Opaque</c>: The block's FROM clause was not understood, so unqualified names cannot be checked.</para>
/// <para><c>OutputsKnown</c>: Whether <see cref="Outputs"/> describes the result columns (false for INTO, FOR XML and similar).</para>
/// <para><c>Members</c>: For a set operation (UNION, INTERSECT, EXCEPT), the member blocks; the first one names the columns.</para>
/// </remarks>
internal sealed record QueryScope(
    int Parent,
    bool Opaque,
    EquatableArray<QuerySource> Sources,
    EquatableArray<ColumnReference> References,
    EquatableArray<QueryOutput> Outputs,
    bool OutputsKnown,
    EquatableArray<int> Members);

/// <summary>An INSERT target and its column list, for the required-column check.</summary>
/// <remarks>
/// <para><c>Columns</c>: The listed columns; with <see cref="AllDefaults"/> (DEFAULT VALUES) there are none.</para>
/// </remarks>
internal sealed record InsertTarget(int Scope, int Source, EquatableArray<SqlName> Columns, bool AllDefaults);

/// <summary>Everything the schema checks know about a contract's SQL.</summary>
/// <remarks>
/// <para><c>Result</c>: Block whose outputs are the contract's result set, or -1 when unknown or not a single statement.</para>
/// <para><c>Unsupported</c>: Why parts of the SQL could not be checked (reported as VQ1011).</para>
/// </remarks>
internal sealed record QueryShape(
    EquatableArray<TableReference> Tables,
    EquatableArray<QueryScope> Scopes,
    int Result,
    EquatableArray<InsertTarget> Inserts,
    EquatableArray<string> Unsupported);

/// <summary>How a mapped member or scalar result reads its column, for the type check.</summary>
internal enum TargetKind
{
    Other,
    Boolean,
    Char,
    Integral,
    Enum,
    Single,
    Double,
    Decimal,
    String,
    Guid,
    DateTime,
    DateTimeOffset,
    DateOnly,
    TimeOnly,
    TimeSpan,
    Binary,
}

/// <summary>A result member the query must supply.</summary>
/// <remarks>
/// <para><c>Required</c>: Constructor and init/required members need their column; settable properties do not.</para>
/// </remarks>
internal sealed record ResultMember(string Member, string Column, string Type, TargetKind Kind, bool IsNullable, bool Required);

/// <summary>Input to the schema checks for one contract.</summary>
/// <remarks>
/// <para><c>RowType</c>: Display name of a mapped row type, or null for scalar and non-query results.</para>
/// <para><c>Members</c>: The row type's members, or a single unnamed member for a scalar result.</para>
/// <para><c>Scalar</c>: Whether the result is a single scalar column.</para>
/// </remarks>
internal sealed record SchemaCheckInput(
    string QueryId,
    SqlDialect Dialect,
    QueryShape? Shape,
    string? SkipReason,
    string? RowType,
    EquatableArray<ResultMember> Members,
    bool Scalar,
    bool ReturnsRows,
    LocationInfo? SqlLocation);
