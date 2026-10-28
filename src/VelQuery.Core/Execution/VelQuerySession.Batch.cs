using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Engine;
using VelQuery.Providers;

namespace VelQuery;

public sealed partial class VelQuerySession
{
    /// <summary>
    /// Creates an empty batch: several commands executed in one round trip (<see cref="VelQueryBatch"/>).
    /// </summary>
    /// <param name="queryId">Stable ID for errors not attributable to one command (for example <c>orders.import</c>).</param>
    public VelQueryBatch CreateBatch(string queryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryId);
        ThrowIfDisposed();
        return new VelQueryBatch(this, queryId);
    }

    internal async Task<IReadOnlyList<int>> ExecuteBatchAsync(VelQueryBatch batch, List<VelQueryBatch.BatchItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        ConnectionLease lease = default;
        DbBatch? dbBatch = null;

        // Each command is built by the normal path (text, type, timeout, transaction, parameter binding, capability
        // checks), so batch commands bind exactly like single commands. With DbBatch support their parameters are then
        // moved onto the batch command and moved back afterwards for output parameters.
        var commands = new DbCommand?[items.Count];
        var failingQueryId = batch.QueryId;
        var faulted = true;
        try
        {
            lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
            var connection = lease.Connection;
            for (var i = 0; i < items.Count; i++)
            {
                failingQueryId = items[i].QueryId;
                commands[i] = Create(connection, items[i]);
            }

            failingQueryId = batch.QueryId;
            var affected = new int[items.Count];
            if (connection.CanCreateBatch && (_provider.Capabilities & ProviderCapabilities.Batches) != 0)
            {
                dbBatch = connection.CreateBatch();
                dbBatch.Transaction = _transaction;
                var timeout = 0;
                foreach (var command in commands)
                {
                    timeout = Math.Max(timeout, command!.CommandTimeout);
                    var batchCommand = dbBatch.CreateBatchCommand();
                    batchCommand.CommandText = command.CommandText;
                    batchCommand.CommandType = command.CommandType;
                    MoveParameters(command.Parameters, batchCommand.Parameters);
                    dbBatch.BatchCommands.Add(batchCommand);
                }

                dbBatch.Timeout = timeout;
                try
                {
                    await dbBatch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbException ex) when (ex.BatchCommand is { } failed)
                {
                    var index = dbBatch.BatchCommands.IndexOf(failed);
                    failingQueryId = index >= 0 ? items[index].QueryId : batch.QueryId;
                    throw;
                }

                for (var i = 0; i < items.Count; i++)
                {
                    affected[i] = dbBatch.BatchCommands[i].RecordsAffected;
                    MoveParameters(dbBatch.BatchCommands[i].Parameters, commands[i]!.Parameters);
                }
            }
            else
            {
                for (var i = 0; i < items.Count; i++)
                {
                    failingQueryId = items[i].QueryId;
                    using var interrupt = RegisterInterrupt(commands[i]!, cancellationToken);
                    affected[i] = await commands[i]!.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                failingQueryId = batch.QueryId;
            }

            for (var i = 0; i < items.Count; i++)
            {
                failingQueryId = items[i].QueryId;
                if (items[i].Text is { } text)
                {
                    text.Complete(commands[i]!, StrictNulls);
                }
            }

            faulted = false;
            return affected;
        }
        catch (Exception ex)
        {
            throw Translate(ex, failingQueryId, cancellationToken);
        }
        finally
        {
            await CleanupBatchAsync(dbBatch, commands, lease, faulted, batch.QueryId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes the batch, every command and the connection lease exactly once. As in <see cref="CleanupAsync"/>,
    /// cleanup failures surface only when execution succeeded, so they never hide the original failure.
    /// </summary>
    private async ValueTask CleanupBatchAsync(DbBatch? dbBatch, DbCommand?[] commands, ConnectionLease lease, bool faulted, string queryId)
    {
        var cleanupFailure = await AsyncDisposal.TryDisposeAsync(dbBatch).ConfigureAwait(false);
        foreach (var command in commands)
        {
            var failure = await AsyncDisposal.TryDisposeAsync(command).ConfigureAwait(false);
            cleanupFailure ??= failure;
        }

        await CleanupAsync(null, null, lease, faulted || cleanupFailure is not null, queryId).ConfigureAwait(false);
        if (!faulted && cleanupFailure is not null)
        {
            throw Translate(cleanupFailure, queryId, CancellationToken.None);
        }
    }

    private DbCommand Create(DbConnection connection, in VelQueryBatch.BatchItem item)
        => item.Text is { } text ? CreateCommand(connection, text) : item.Descriptor!.CreateCommand(this, connection);

    /// <summary>A parameter belongs to one collection at a time, so it is removed before it is added elsewhere.</summary>
    private static void MoveParameters(DbParameterCollection from, DbParameterCollection to)
    {
        while (from.Count > 0)
        {
            var parameter = from[0];
            from.RemoveAt(0);
            to.Add(parameter);
        }
    }
}
