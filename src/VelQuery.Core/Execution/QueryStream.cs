using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Mapping;

namespace VelQuery.Execution;

/// <summary>
/// Streams rows one at a time (VQ-FR-013). Each enumeration executes the command once; the enumerator owns the
/// reader, command and any VelQuery-owned connection until it completes or is disposed, including on early exit,
/// consumer exceptions and cancellation (VQ-FR-027).
/// </summary>
internal sealed class QueryStream<TSpec, TRow>(VelQuerySession session, TSpec spec, IRowMapperFactory<TRow> factory, CancellationToken callToken)
    : IAsyncEnumerable<TRow>
    where TSpec : struct, ICommandSpec
{
    public IAsyncEnumerator<TRow> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new Enumerator(session, spec, factory, callToken, cancellationToken);

    private sealed class Enumerator : IAsyncEnumerator<TRow>
    {
        private readonly VelQuerySession _session;
        private readonly TSpec _spec;
        private readonly IRowMapperFactory<TRow> _factory;
        private readonly CancellationTokenSource? _linked;
        private readonly CancellationToken _token;
        private ConnectionLease _lease;
        private DbCommand? _command;
        private DbDataReader? _reader;
        private IRowMapper<TRow>? _mapper;
        private bool _started;
        private bool _finished;

        public Enumerator(VelQuerySession session, TSpec spec, IRowMapperFactory<TRow> factory, CancellationToken callToken, CancellationToken enumeratorToken)
        {
            _session = session;
            _spec = spec;
            _factory = factory;
            if (callToken.CanBeCanceled && enumeratorToken.CanBeCanceled && callToken != enumeratorToken)
            {
                _linked = CancellationTokenSource.CreateLinkedTokenSource(callToken, enumeratorToken);
                _token = _linked.Token;
            }
            else
            {
                _token = callToken.CanBeCanceled ? callToken : enumeratorToken;
            }
        }

        public TRow Current { get; private set; } = default!;

        /// <summary>
        /// Fast path: when the driver already has the next row buffered, <c>ReadAsync</c> completes synchronously and the
        /// row is mapped without entering an async state machine, so steady-state streaming allocates nothing per row
        /// beyond the mapped value.
        /// </summary>
        public ValueTask<bool> MoveNextAsync()
        {
            if (_started && !_finished && _reader is { } reader)
            {
                Task<bool> read;
                try
                {
                    read = reader.ReadAsync(_token);
                }
                catch (Exception ex)
                {
                    return FailAsync(ex);
                }

                if (read.IsCompletedSuccessfully && read.Result)
                {
                    try
                    {
                        Current = _mapper!.Map(reader);
                        return new ValueTask<bool>(true);
                    }
                    catch (Exception ex)
                    {
                        return FailAsync(ex);
                    }
                }

                return MoveNextSlowAsync(read);
            }

            return MoveNextSlowAsync(null);
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private async ValueTask<bool> MoveNextSlowAsync(Task<bool>? pendingRead)
        {
            if (_finished)
            {
                return false;
            }

            try
            {
                if (!_started)
                {
                    _started = true;
                    _lease = await _session.AcquireAsync(_token).ConfigureAwait(false);
                    _command = _session.CreateCommand(_lease.Connection, _spec);
                    _reader = await _command.ExecuteReaderAsync(VelQuerySession.ReaderBehavior(_factory, CommandBehavior.Default), _token).ConfigureAwait(false);
                    _mapper = _factory.Create(_reader, _session.MappingContext(_spec.QueryId));
                }

                // A read issued by the fast path must be awaited, never re-issued.
                if (await (pendingRead ?? _reader!.ReadAsync(_token)).ConfigureAwait(false))
                {
                    Current = _mapper!.Map(_reader!);
                    return true;
                }

                while (await _reader!.NextResultAsync(_token).ConfigureAwait(false))
                {
                }

                // Clear before disposing so a throwing Dispose is not retried by cleanup (dispose exactly once).
                var reader = _reader;
                _reader = null;
                await reader.DisposeAsync().ConfigureAwait(false);
                _spec.Complete(_command!, _session.StrictNulls);
                Current = default!;
                await FinishAsync(faulted: false).ConfigureAwait(false);
                return false;
            }
            catch (Exception ex)
            {
                await FinishAsync(faulted: true).ConfigureAwait(false);
                throw _session.Translate(ex, _spec.QueryId, _token);
            }
        }

        private async ValueTask<bool> FailAsync(Exception exception)
        {
            await FinishAsync(faulted: true).ConfigureAwait(false);
            throw _session.Translate(exception, _spec.QueryId, _token);
        }

        public ValueTask DisposeAsync()
        {
            // Early exit (break, consumer exception) leaves rows unread: treat as faulted so the command is cancelled
            // instead of draining the remaining rows.
            return FinishAsync(faulted: true);
        }

        private async ValueTask FinishAsync(bool faulted)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            try
            {
                if (_started)
                {
                    await _session.CleanupAsync(_reader, _command, _lease, faulted, _spec.QueryId).ConfigureAwait(false);
                }
            }
            finally
            {
                _reader = null;
                _command = null;
                _linked?.Dispose();
            }
        }
    }
}
