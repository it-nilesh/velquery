using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Mapping;

namespace VelQuery;

/// <summary>
/// Consumes the result sets of one command in order (VQ-FR-021). Single-consumer and not thread-safe. Disposing
/// before all results are read cancels the command and releases every resource.
/// </summary>
public sealed class VelQueryMultipleReader : IAsyncDisposable
{
    private readonly VelQuerySession _session;
    private readonly ConnectionLease _lease;
    private readonly DbCommand _command;
    private readonly Action<DbCommand, bool> _complete;
    private DbDataReader? _reader;
    private int _index;
    private bool _hasCurrent;
    private bool _faulted;
    private int _busy;
    private bool _disposed;

    internal VelQueryMultipleReader(VelQuerySession session, ConnectionLease lease, DbCommand command, DbDataReader reader, string queryId, Action<DbCommand, bool> complete)
    {
        _session = session;
        _lease = lease;
        _command = command;
        _reader = reader;
        // A reader with no columns means the command produced no result sets at all (e.g. only DML statements).
        _hasCurrent = reader.FieldCount > 0;
        QueryId = queryId;
        _complete = complete;
    }

    /// <summary>The query identity.</summary>
    public string QueryId { get; }

    /// <summary>Zero-based index of the next result set to be read.</summary>
    public int ResultIndex => _index;

    /// <summary>Whether another result set is available.</summary>
    public bool HasMoreResults => _hasCurrent && !_disposed;

    /// <summary>Reads the next result set using the registered mapper for <typeparamref name="TRow"/>.</summary>
    public Task<IReadOnlyList<TRow>> ReadAsync<[DynamicallyAccessedMembers(ReflectionRowMapperFactory.RequiredMembers)] TRow>(CancellationToken cancellationToken = default)
        => ReadAsync(_session.ResolveMapper<TRow>(QueryId), cancellationToken);

    /// <summary>Reads the next result set using <paramref name="mapper"/>.</summary>
    public async Task<IReadOnlyList<TRow>> ReadAsync<TRow>(IRowMapperFactory<TRow> mapper, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        Enter();
        try
        {
            var reader = CurrentReader();
            using var interrupt = _session.RegisterInterrupt(_command, cancellationToken);
            var rowMapper = mapper.Create(reader, _session.MappingContext(QueryId));
            var rows = new List<TRow>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(rowMapper.Map(reader));
            }

            await AdvanceAsync(reader, cancellationToken).ConfigureAwait(false);
            return rows;
        }
        catch (Exception ex)
        {
            _faulted = true;
            throw _session.Translate(ex, QueryId, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Reads exactly one row from the next result set.</summary>
    public async Task<TRow> ReadSingleAsync<TRow>(IRowMapperFactory<TRow> mapper, CancellationToken cancellationToken = default)
    {
        var rows = await ReadAsync(mapper, cancellationToken).ConfigureAwait(false);
        return rows.Count switch
        {
            0 => throw new VelQueryNoRowsException(QueryId),
            1 => rows[0],
            _ => throw new VelQueryTooManyRowsException(QueryId),
        };
    }

    /// <summary>Reads exactly one row from the next result set using the registered mapper.</summary>
    public Task<TRow> ReadSingleAsync<[DynamicallyAccessedMembers(ReflectionRowMapperFactory.RequiredMembers)] TRow>(CancellationToken cancellationToken = default)
        => ReadSingleAsync(_session.ResolveMapper<TRow>(QueryId), cancellationToken);

    private DbDataReader CurrentReader()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_hasCurrent || _reader is null)
        {
            throw new VelQueryValidationException(QueryId, $"result-shape error: result set {_index} was requested but the command returned only {_index} result set(s).");
        }

        return _reader;
    }

    private async ValueTask AdvanceAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        _index++;
        _hasCurrent = await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        if (!_hasCurrent)
        {
            // Clear before disposing so a throwing Dispose is not retried by DisposeAsync (dispose exactly once).
            _reader = null;
            await reader.DisposeAsync().ConfigureAwait(false);
            _complete(_command, _session.StrictNulls);
        }
    }

    private void Enter()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new VelQueryValidationException(QueryId, "a multi-result reader is single-consumer; await the previous read before starting another.");
        }
    }

    /// <summary>Releases the reader, command and any VelQuery-owned connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var reader = _reader;
        _reader = null;
        await _session.CleanupAsync(reader, _command, _lease, faulted: _faulted || reader is not null, QueryId).ConfigureAwait(false);
    }
}
