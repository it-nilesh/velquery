using System;
using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace VelQuery;

/// <summary>
/// Declares a static query contract (VQ-FR-031). Apply to a <see langword="partial"/> class that declares exactly one
/// <see langword="partial"/> method without a body; VelQuery.Generators emits a reflection-free descriptor, parameter
/// binder, row mapper and the method implementation.
/// </summary>
/// <remarks>
/// <para>The method's first parameter must be an <c>IVelQuerySession</c> (or <c>VelQuerySession</c>); an optional
/// <see cref="System.Threading.CancellationToken"/> may be the last parameter. Every other parameter binds to the SQL
/// placeholder with the same name, compared case-insensitively (<c>long id</c> binds <c>@Id</c>). Only placeholders
/// that actually occur in the SQL (outside literals, quoted identifiers and comments) are bound.</para>
/// <para>The operation is chosen from the return type and <see cref="Cardinality"/>. Combinations not listed are
/// rejected with a diagnostic rather than guessed:</para>
/// <list type="table">
/// <listheader><term>Return type and cardinality</term><description>Operation</description></listheader>
/// <item><term><c>Task&lt;IReadOnlyList&lt;T&gt;&gt;</c>, <see cref="QueryCardinality.Default"/> or <see cref="QueryCardinality.Many"/></term><description><c>QueryAsync</c></description></item>
/// <item><term><c>IAsyncEnumerable&lt;T&gt;</c>, <see cref="QueryCardinality.Default"/> or <see cref="QueryCardinality.Many"/></term><description><c>StreamAsync</c></description></item>
/// <item><term><c>Task&lt;int&gt;</c>, <see cref="QueryCardinality.NonQuery"/></term><description><c>ExecuteAsync</c> (affected rows)</description></item>
/// <item><term><c>Task&lt;T?&gt;</c>, <see cref="QueryCardinality.Default"/></term><description><c>QuerySingleOrDefaultAsync</c></description></item>
/// <item><term><c>Task&lt;T&gt;</c> (non-nullable), <see cref="QueryCardinality.Default"/></term><description><c>QuerySingleAsync</c></description></item>
/// <item><term><c>Task&lt;T&gt;</c>, <see cref="QueryCardinality.First"/> or <see cref="QueryCardinality.Single"/></term><description><c>QueryFirstAsync</c> or <c>QuerySingleAsync</c></description></item>
/// <item><term><c>Task&lt;T?&gt;</c>, <see cref="QueryCardinality.FirstOrDefault"/> or <see cref="QueryCardinality.SingleOrDefault"/></term><description><c>QueryFirstOrDefaultAsync</c> or <c>QuerySingleOrDefaultAsync</c></description></item>
/// <item><term><c>Task&lt;T&gt;</c> with scalar <c>T</c>, <see cref="QueryCardinality.Scalar"/></term><description><c>ExecuteScalarAsync</c></description></item>
/// </list>
/// <para><c>Task&lt;int&gt;</c> with <see cref="QueryCardinality.Default"/> is ambiguous (affected rows or a one-row
/// integer result) and requires an explicit cardinality, as does a reference-type result declared in a
/// nullable-oblivious context.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class VelQuerySqlAttribute : Attribute
{
    /// <summary>Declares a query contract.</summary>
    /// <param name="queryId">Stable, low-cardinality diagnostic identity (VQ-FR-025). Must not contain values.</param>
    /// <param name="sql">Constant SQL text, or a stored procedure name with <see cref="CommandType"/>.</param>
    public VelQuerySqlAttribute(string queryId, string sql)
    {
        QueryId = queryId;
        Sql = sql;
    }

    /// <summary>Stable diagnostic identity.</summary>
    public string QueryId { get; }

    /// <summary>Constant SQL text or stored procedure name.</summary>
    public string Sql { get; }

    /// <summary>Overrides the operation inferred from the method's return type. See the type remarks.</summary>
    public QueryCardinality Cardinality { get; set; }

    /// <summary>The command type; defaults to <see cref="System.Data.CommandType.Text"/>.</summary>
    public CommandType CommandType { get; set; } = CommandType.Text;

    /// <summary>Default command timeout in seconds; <c>0</c> (the default) uses the session's timeout.</summary>
    public int CommandTimeoutSeconds { get; set; }
}

/// <summary>Result cardinality of a <see cref="VelQuerySqlAttribute"/> contract.</summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "Names mirror the QuerySingleAsync/QueryFirstAsync operation vocabulary (PRD §8.1).")]
public enum QueryCardinality
{
    /// <summary>Inferred from the return type; ambiguous shapes are rejected at compile time.</summary>
    Default,

    /// <summary>All rows, buffered (<c>Task&lt;IReadOnlyList&lt;T&gt;&gt;</c>) or streamed (<c>IAsyncEnumerable&lt;T&gt;</c>).</summary>
    Many,

    /// <summary>The first row; no rows throws <see cref="VelQueryNoRowsException"/>.</summary>
    First,

    /// <summary>The first row or <see langword="null"/>; requires a nullable result type.</summary>
    FirstOrDefault,

    /// <summary>The only row; zero or multiple rows throw.</summary>
    Single,

    /// <summary>The only row or <see langword="null"/>; multiple rows throw. Requires a nullable result type.</summary>
    SingleOrDefault,

    /// <summary>The first column of the first row via <c>ExecuteScalarAsync</c>; requires a scalar result type.</summary>
    Scalar,

    /// <summary>A command without a result set; returns the provider-reported affected row count (<c>Task&lt;int&gt;</c>).</summary>
    NonQuery,
}

/// <summary>
/// Overrides the parameter metadata VelQuery.Generators infers for a contract method parameter (VQ-FR-008). Only
/// properties that are explicitly set are applied; the generator reads them at compile time and nothing reads them at
/// run time.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class VelQueryParameterAttribute : Attribute
{
    /// <summary>The SQL placeholder name (without prefix) when it differs from the C# parameter name.</summary>
    public string? Name { get; set; }

    /// <summary>The generic database type; required for CLR types VelQuery cannot infer.</summary>
    public DbType DbType { get; set; }

    /// <summary>Maximum size for strings and binary values; <c>-1</c> means MAX.</summary>
    public int Size { get; set; }

    /// <summary>Numeric precision.</summary>
    public byte Precision { get; set; }

    /// <summary>Numeric scale.</summary>
    public byte Scale { get; set; }
}
