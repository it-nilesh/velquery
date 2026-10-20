using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Execution;
using VelQuery.Mapping;
using VelQuery.Providers;

namespace VelQuery;

public sealed partial class VelQuerySession
{
    /// <inheritdoc />
    public async Task<TResult> ExecuteOnConnectionAsync<TState, TResult>(string queryId, TState state,
        Func<ConnectionContext, TState, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryId);
        ArgumentNullException.ThrowIfNull(operation);
        ConnectionLease lease = default;
        var faulted = true;
        try
        {
            lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
            int? timeout = (CommandTimeout ?? _options.DefaultTimeout) is { } t ? CommandTimeouts.ToSeconds(t, queryId) : null;
            var result = await operation(new ConnectionContext(lease.Connection, _transaction, timeout), state, cancellationToken).ConfigureAwait(false);
            faulted = false;
            return result;
        }
        catch (Exception ex)
        {
            throw Translate(ex, queryId, cancellationToken);
        }
        finally
        {
            await CleanupAsync(null, null, lease, faulted, queryId).ConfigureAwait(false);
        }
    }

    internal async Task<IReadOnlyList<TRow>> BufferAsync<TSpec, TRow>(TSpec spec, IRowMapperFactory<TRow> factory, CancellationToken cancellationToken)
        where TSpec : struct, ICommandSpec
    {
        ConnectionLease lease = default;
        DbCommand? command = null;
        DbDataReader? reader = null;
        var rows = default(RowBuffer<TRow>);
        var faulted = true;
        try
        {
            lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
            command = CreateCommand(lease.Connection, spec);
            using var interrupt = RegisterInterrupt(command, cancellationToken);
            reader = await command.ExecuteReaderAsync(ReaderBehavior(factory, CommandBehavior.Default), cancellationToken).ConfigureAwait(false);
            var mapper = factory.Create(reader, MappingContext(spec.QueryId));
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(mapper.Map(reader));
            }

            await DrainAsync(reader, cancellationToken).ConfigureAwait(false);
            // Clear before disposing so a throwing Dispose is not retried by cleanup (dispose exactly once).
            var completed = reader;
            reader = null;
            await completed.DisposeAsync().ConfigureAwait(false);
            spec.Complete(command, StrictNulls);
            faulted = false;
            return rows.ToResult();
        }
        catch (Exception ex)
        {
            throw Translate(ex, spec.QueryId, cancellationToken);
        }
        finally
        {
            rows.Dispose();
            await CleanupAsync(reader, command, lease, faulted, spec.QueryId).ConfigureAwait(false);
        }
    }

    internal async Task<TRow?> ReadOneAsync<TSpec, TRow>(TSpec spec, IRowMapperFactory<TRow> factory, RowCardinality cardinality, CancellationToken cancellationToken)
        where TSpec : struct, ICommandSpec
    {
        ConnectionLease lease = default;
        DbCommand? command = null;
        DbDataReader? reader = null;
        var faulted = true;
        var isFirst = cardinality is RowCardinality.First or RowCardinality.FirstOrDefault;
        try
        {
            lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
            command = CreateCommand(lease.Connection, spec);
            using var interrupt = RegisterInterrupt(command, cancellationToken);
            reader = await command.ExecuteReaderAsync(ReaderBehavior(factory, isFirst ? CommandBehavior.SingleRow : CommandBehavior.Default), cancellationToken).ConfigureAwait(false);
            var mapper = factory.Create(reader, MappingContext(spec.QueryId));
            TRow? result;
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (cardinality is RowCardinality.First or RowCardinality.Single)
                {
                    throw new VelQueryNoRowsException(spec.QueryId);
                }

                result = default;
            }
            else
            {
                result = mapper.Map(reader);
                if (!isFirst && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new VelQueryTooManyRowsException(spec.QueryId);
                }
            }

            if (!isFirst)
            {
                await DrainAsync(reader, cancellationToken).ConfigureAwait(false);
            }

            // Clear before disposing so a throwing Dispose is not retried by cleanup (dispose exactly once).
            var completed = reader;
            reader = null;
            await completed.DisposeAsync().ConfigureAwait(false);
            spec.Complete(command, StrictNulls);
            faulted = false;
            return result;
        }
        catch (Exception ex)
        {
            throw Translate(ex, spec.QueryId, cancellationToken);
        }
        finally
        {
            await CleanupAsync(reader, command, lease, faulted, spec.QueryId).ConfigureAwait(false);
        }
    }

    internal async Task<int> ExecuteNonQueryAsync<TSpec>(TSpec spec, CancellationToken cancellationToken)
        where TSpec : struct, ICommandSpec
    {
        ConnectionLease lease = default;
        DbCommand? command = null;
        var faulted = true;
        try
        {
            lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
            command = CreateCommand(lease.Connection, spec);
            using var interrupt = RegisterInterrupt(command, cancellationToken);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            spec.Complete(command, StrictNulls);
            faulted = false;
            return affected;
        }
        catch (Exception ex)
        {
            throw Translate(ex, spec.QueryId, cancellationToken);
        }
        finally
        {
            await CleanupAsync(null, command, lease, faulted, spec.QueryId).ConfigureAwait(false);
        }
    }

    internal async Task<T> ExecuteScalarCoreAsync<TSpec, T>(TSpec spec, CancellationToken cancellationToken)
        where TSpec : struct, ICommandSpec
    {
        if (NativeConversions)
        {
            // ExecuteScalar returns an untyped object; read the first cell through the driver's typed getter instead.
            // No rows: default for nullable targets, VelQueryNoRowsException otherwise (same as below).
            var cardinality = default(T) is null ? RowCardinality.FirstOrDefault : RowCardinality.First;
            return (await ReadOneAsync(spec, ScalarRowMapperFactory<T>.Instance, cardinality, cancellationToken).ConfigureAwait(false))!;
        }

        ConnectionLease lease = default;
        DbCommand? command = null;
        var faulted = true;
        try
        {
            lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
            command = CreateCommand(lease.Connection, spec);
            using var interrupt = RegisterInterrupt(command, cancellationToken);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            spec.Complete(command, StrictNulls);
            T result;
            if (value is null)
            {
                // No rows at all (distinct from a NULL first cell).
                result = default(T) is null ? default! : throw new VelQueryNoRowsException(spec.QueryId);
            }
            else
            {
                result = ValueConverter.Convert<T>(value, spec.QueryId, column: null, StrictNulls);
            }

            faulted = false;
            return result;
        }
        catch (Exception ex)
        {
            throw Translate(ex, spec.QueryId, cancellationToken);
        }
        finally
        {
            await CleanupAsync(null, command, lease, faulted, spec.QueryId).ConfigureAwait(false);
        }
    }

    internal async Task<VelQueryMultipleReader> OpenMultipleAsync<TSpec>(TSpec spec, CancellationToken cancellationToken)
        where TSpec : struct, ICommandSpec
    {
        RequireCapability(ProviderCapabilities.MultipleResults, "multiple result sets", spec.QueryId);
        ConnectionLease lease = default;
        DbCommand? command = null;
        DbDataReader? reader = null;
        try
        {
            lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
            command = CreateCommand(lease.Connection, spec);
            reader = await command.ExecuteReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
            return new VelQueryMultipleReader(this, lease, command, reader, spec.QueryId, (cmd, strict) => spec.Complete(cmd, strict));
        }
        catch (Exception ex)
        {
            await CleanupAsync(reader, command, lease, faulted: true, spec.QueryId).ConfigureAwait(false);
            throw Translate(ex, spec.QueryId, cancellationToken);
        }
    }

    /// <summary>
    /// Adds <see cref="CommandBehavior.SequentialAccess"/> when the mapper reads columns strictly in order, so the driver
    /// can stream column values instead of buffering each row.
    /// </summary>
    internal static CommandBehavior ReaderBehavior<TRow>(IRowMapperFactory<TRow> factory, CommandBehavior behavior)
        => factory.SupportsSequentialAccess ? behavior | CommandBehavior.SequentialAccess : behavior;

    /// <summary>Consumes trailing result sets so errors raised by later statements in a batch are not lost.</summary>
    private static async ValueTask DrainAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
        }
    }
}

internal enum RowCardinality
{
    First,
    FirstOrDefault,
    Single,
    SingleOrDefault,
}
