using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Execution;
using VelQuery.Mapping;

namespace VelQuery;

/// <summary>
/// A runtime SQL command with per-call options, created by <see cref="VelQuerySession.Sql"/>. Immutable value type;
/// <c>With*</c> methods return modified copies.
/// </summary>
public readonly struct VelQueryTextCommand
{
    private const DynamicallyAccessedMemberTypes Members = ReflectionRowMapperFactory.RequiredMembers;

    private readonly VelQuerySession _session;
    private readonly string _sql;
    private readonly QueryParameters? _parameters;
    private readonly string? _queryId;
    private readonly TimeSpan? _timeout;
    private readonly CommandType _commandType;

    internal VelQueryTextCommand(VelQuerySession session, string sql, QueryParameters? parameters,
        string? queryId = null, TimeSpan? timeout = null, CommandType commandType = CommandType.Text)
    {
        _session = session;
        _sql = sql;
        _parameters = parameters;
        _queryId = queryId;
        _timeout = timeout;
        _commandType = commandType;
    }

    /// <summary>Overrides the command timeout for this call (VQ-FR-010).</summary>
    public VelQueryTextCommand WithTimeout(TimeSpan timeout)
    {
        CommandTimeouts.Validate(timeout, _queryId);
        return new(_session, _sql, _parameters, _queryId, timeout, _commandType);
    }

    /// <summary>Sets a stable diagnostic query identity (VQ-FR-025). Must not contain parameter values.</summary>
    public VelQueryTextCommand WithQueryId(string queryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryId);
        return new(_session, _sql, _parameters, queryId, _timeout, _commandType);
    }

    /// <summary>Treats the SQL text as a stored procedure name (VQ-FR-023).</summary>
    public VelQueryTextCommand AsStoredProcedure()
        => new(_session, _sql, _parameters, _queryId, _timeout, CommandType.StoredProcedure);

    private TextSpec Spec => new(_sql, _parameters, _queryId, _timeout, _commandType);

    /// <summary>Buffers all rows (VQ-FR-002).</summary>
    public Task<IReadOnlyList<T>> QueryAsync<[DynamicallyAccessedMembers(Members)] T>(CancellationToken cancellationToken = default)
    {
        var spec = Spec;
        return _session.BufferAsync(spec, _session.ResolveMapper<T>(spec.QueryId), cancellationToken);
    }

    /// <summary>Returns the first row or throws <see cref="VelQueryNoRowsException"/> (VQ-FR-003).</summary>
    public Task<T> QueryFirstAsync<[DynamicallyAccessedMembers(Members)] T>(CancellationToken cancellationToken = default)
        => One<T>(RowCardinality.First, cancellationToken)!;

    /// <summary>Returns the first row or default.</summary>
    public Task<T?> QueryFirstOrDefaultAsync<[DynamicallyAccessedMembers(Members)] T>(CancellationToken cancellationToken = default)
        => One<T>(RowCardinality.FirstOrDefault, cancellationToken);

    /// <summary>Returns the only row; throws for zero or multiple rows (VQ-FR-004).</summary>
    public Task<T> QuerySingleAsync<[DynamicallyAccessedMembers(Members)] T>(CancellationToken cancellationToken = default)
        => One<T>(RowCardinality.Single, cancellationToken)!;

    /// <summary>Returns the only row or default; throws for multiple rows.</summary>
    public Task<T?> QuerySingleOrDefaultAsync<[DynamicallyAccessedMembers(Members)] T>(CancellationToken cancellationToken = default)
        => One<T>(RowCardinality.SingleOrDefault, cancellationToken);

    /// <summary>Streams rows one at a time (VQ-FR-013).</summary>
    public IAsyncEnumerable<T> StreamAsync<[DynamicallyAccessedMembers(Members)] T>(CancellationToken cancellationToken = default)
    {
        var spec = Spec;
        return new QueryStream<TextSpec, T>(_session, spec, _session.ResolveMapper<T>(spec.QueryId), cancellationToken);
    }

    /// <summary>Executes and returns the provider-reported affected row count (VQ-FR-006).</summary>
    public Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
        => _session.ExecuteNonQueryAsync(Spec, cancellationToken);

    /// <summary>
    /// Returns the first column of the first row (VQ-FR-005). With no rows, nullable targets receive default and
    /// non-nullable targets throw <see cref="VelQueryNoRowsException"/>.
    /// </summary>
    public Task<T> ExecuteScalarAsync<T>(CancellationToken cancellationToken = default)
        => _session.ExecuteScalarCoreAsync<TextSpec, T>(Spec, cancellationToken);

    /// <summary>Opens a reader over multiple result sets (VQ-FR-021).</summary>
    public Task<VelQueryMultipleReader> QueryMultipleAsync(CancellationToken cancellationToken = default)
        => _session.OpenMultipleAsync(Spec, cancellationToken);

    private Task<T?> One<[DynamicallyAccessedMembers(Members)] T>(RowCardinality cardinality, CancellationToken cancellationToken)
    {
        var spec = Spec;
        return _session.ReadOneAsync(spec, _session.ResolveMapper<T>(spec.QueryId), cardinality, cancellationToken);
    }
}
