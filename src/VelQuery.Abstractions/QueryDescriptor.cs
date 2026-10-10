using System;
using System.Data;
using VelQuery.Mapping;
using VelQuery.Parameters;

namespace VelQuery;

/// <summary>
/// Immutable, thread-safe description of a static query (VQ-FR-028): identity, SQL text, command type and a binder.
/// Descriptors are usually emitted by VelQuery.Generators but may be written by hand.
/// </summary>
/// <typeparam name="TArgs">Argument type bound by <see cref="Binder"/>. Use <see cref="NoArgs"/> when there are none.</typeparam>
public class QueryDescriptor<TArgs>
{
    /// <summary>Creates a descriptor.</summary>
    public QueryDescriptor(string queryId, string sql, ParameterBinder<TArgs>? binder = null,
        CommandType commandType = CommandType.Text, TimeSpan? commandTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        QueryId = queryId;
        Sql = sql;
        Binder = binder;
        CommandType = commandType;
        CommandTimeout = commandTimeout;
    }

    /// <summary>Stable, low-cardinality diagnostic identity. Never contains parameter values (VQ-FR-025).</summary>
    public string QueryId { get; }

    /// <summary>Static SQL text or stored procedure name.</summary>
    public string Sql { get; }

    /// <summary>Binds arguments to the command, or <see langword="null"/> when the query has no parameters.</summary>
    public ParameterBinder<TArgs>? Binder { get; }

    /// <summary>The command type.</summary>
    public CommandType CommandType { get; }

    /// <summary>Default timeout for this query; overridden by per-call options.</summary>
    public TimeSpan? CommandTimeout { get; }

    /// <inheritdoc />
    public override string ToString() => QueryId;
}

/// <summary>A query descriptor that also knows how to map result rows.</summary>
public sealed class QueryDescriptor<TArgs, TRow> : QueryDescriptor<TArgs>
{
    /// <summary>Creates a descriptor with an explicit row mapper factory.</summary>
    public QueryDescriptor(string queryId, string sql, IRowMapperFactory<TRow> mapper, ParameterBinder<TArgs>? binder = null,
        CommandType commandType = CommandType.Text, TimeSpan? commandTimeout = null)
        : base(queryId, sql, binder, commandType, commandTimeout)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        Mapper = mapper;
    }

    /// <summary>The row mapper factory.</summary>
    public IRowMapperFactory<TRow> Mapper { get; }
}

/// <summary>Argument type for queries without parameters.</summary>
public readonly record struct NoArgs;
