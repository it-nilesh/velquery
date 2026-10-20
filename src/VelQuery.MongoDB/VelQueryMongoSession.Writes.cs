using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;

namespace VelQuery.MongoDB;

public sealed partial class VelQueryMongoSession
{
    /// <summary>Inserts one document with the contract's generated serializer.</summary>
    public async Task InsertOneAsync<TDocument>(MongoInsertDescriptor<TDocument> command, TDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        var operation = Begin(command, OperationKind.Write, cancellationToken);
        IEnumerator<TDocument>? single = null;
        try
        {
            single = ((IEnumerable<TDocument>)[document]).GetEnumerator();
            single.MoveNext();
            await MongoWriteCommands.InsertAsync(_dataSource.Database, command.CollectionName, single, command.Serializer,
                Limits(command, operation), nameof(document), operation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Translate(ex, command.QueryId, operation, cancellationToken);
        }
        finally
        {
            single?.Dispose();
            operation.Dispose();
        }
    }

    /// <summary>
    /// Bulk-inserts documents with ordered <c>insert</c> commands (batches of up to 100,000 documents and 16 MiB, built
    /// directly into pooled buffers with the generated serializer) and returns the inserted count the server reports.
    /// The operation timeout is enforced by the server for each batch (<c>maxTimeMS</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Partial failure.</b> Outside a transaction MongoDB is <em>not</em> all-or-nothing: documents before the
    /// first failing one stay inserted and the rest are not attempted (ordered). The failure is a
    /// <see cref="VelQueryProviderException"/> whose inner <see cref="MongoWriteCommandException"/> reports the inserted
    /// count and the write error (index across the whole sequence and code, for example 11000, duplicate key).</para>
    /// <para>Inside <see cref="BeginTransactionAsync"/> the insert is part of the transaction: after a failure nothing
    /// is committed, and the server aborts the transaction (it can only be disposed or rolled back). Very large inserts
    /// may exceed transaction limits (by default 60 seconds; oplog entry size), so prefer bulk loads outside
    /// transactions when atomicity is not required.</para>
    /// <para>The sequence is enumerated once, lazily; an empty sequence returns 0 without contacting the server.</para>
    /// </remarks>
    public async Task<long> InsertManyAsync<TDocument>(MongoInsertDescriptor<TDocument> command, IEnumerable<TDocument> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(documents);
        var operation = Begin(command, OperationKind.Write, cancellationToken);
        IEnumerator<TDocument>? enumerator = null;
        try
        {
            var limits = Limits(command, operation);
            enumerator = documents.GetEnumerator();
            if (!enumerator.MoveNext())
            {
                return 0;
            }

            return await MongoWriteCommands.InsertAsync(_dataSource.Database, command.CollectionName, enumerator, command.Serializer,
                limits, nameof(documents), operation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Translate(ex, command.QueryId, operation, cancellationToken);
        }
        finally
        {
            enumerator?.Dispose();
            operation.Dispose();
        }
    }

    /// <summary>Runs an <c>update</c> command and returns the counts the server reports.</summary>
    public async Task<VelQueryMongoUpdateResult> UpdateAsync<TArgs>(MongoUpdateDescriptor<TArgs> command, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operation = Begin(command, OperationKind.Write, cancellationToken);
        try
        {
            return await MongoWriteCommands.UpdateAsync(_dataSource.Database, command.CollectionName, command.Filter(in args), command.Update(in args),
                command.Many, command.Upsert, Limits(command, operation), operation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Translate(ex, command.QueryId, operation, cancellationToken);
        }
        finally
        {
            operation.Dispose();
        }
    }

    /// <summary>Runs a <c>delete</c> command and returns the deleted count the server reports.</summary>
    public async Task<long> DeleteAsync<TArgs>(MongoDeleteDescriptor<TArgs> command, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operation = Begin(command, OperationKind.Write, cancellationToken);
        try
        {
            return await MongoWriteCommands.DeleteAsync(_dataSource.Database, command.CollectionName, command.Filter(in args), command.Many,
                Limits(command, operation), operation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Translate(ex, command.QueryId, operation, cancellationToken);
        }
        finally
        {
            operation.Dispose();
        }
    }

    /// <summary>Server time limit, session and write concern for one write; rejects unacknowledged write concerns.</summary>
    private WriteLimits Limits(MongoOperationDescriptor command, in Operation operation)
    {
        var writeConcern = _dataSource.GetCollection(command.CollectionName).Settings.WriteConcern;
        EnsureCountsReported(writeConcern, operation.Session, command.QueryId);
        return WriteLimits.For(operation.Session, operation.MaxTime, writeConcern);
    }

    /// <summary>
    /// Rejects count-returning writes under an unacknowledged write concern (<c>w: 0</c>) before anything is sent: the
    /// server would report no counts, and VelQuery never invents them (AGENTS rule 3).
    /// </summary>
    private static void EnsureCountsReported(WriteConcern writeConcern, IClientSessionHandle? session, string queryId)
    {
        if (session is { IsInTransaction: true })
        {
            return; // Operations in a transaction are acknowledged; the transaction's write concern applies at commit.
        }

        if (!writeConcern.IsAcknowledged)
        {
            throw new VelQueryUnsupportedCapabilityException(queryId,
                "the write concern is unacknowledged (w: 0), so the server reports no counts; use an acknowledged write concern.");
        }
    }

}
