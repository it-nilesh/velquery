using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
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
        try
        {
            var collection = _dataSource.GetCollection(command.CollectionName);

            // The wrapper serializes lazily with VelQuery's serializer when the driver writes the command: no
            // intermediate BsonDocument, no class map, no registry entry.
            var wrapped = new BsonDocumentWrapper(document, command.Serializer);
            if (operation.Session is null)
            {
                await collection.InsertOneAsync(wrapped, null, operation.Token).ConfigureAwait(false);
            }
            else
            {
                await collection.InsertOneAsync(operation.Session, wrapped, null, operation.Token).ConfigureAwait(false);
            }
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

    /// <summary>
    /// Bulk-inserts documents with one ordered <c>bulkWrite</c> (the driver splits it into batches within the server's
    /// message limits) and returns the inserted count the server reports.
    /// </summary>
    /// <remarks>
    /// <para><b>Partial failure.</b> Outside a transaction MongoDB is <em>not</em> all-or-nothing: documents before the
    /// first failing one stay inserted and the rest are not attempted (ordered). The failure is a
    /// <see cref="VelQueryProviderException"/> whose inner <see cref="MongoBulkWriteException"/> reports the server's
    /// <c>InsertedCount</c> and the write error (for example code 11000, duplicate key).</para>
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
            var collection = _dataSource.GetCollection(command.CollectionName);
            EnsureCountsReported(collection, operation.Session, command.QueryId);
            enumerator = documents.GetEnumerator();
            if (!enumerator.MoveNext())
            {
                return 0;
            }

            var models = Models(enumerator, command.Serializer, nameof(documents));
            var options = new BulkWriteOptions { IsOrdered = true };
            var result = operation.Session is null
                ? await collection.BulkWriteAsync(models, options, operation.Token).ConfigureAwait(false)
                : await collection.BulkWriteAsync(operation.Session, models, options, operation.Token).ConfigureAwait(false);
            return result.InsertedCount;
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

    /// <summary>Runs <c>updateOne</c>/<c>updateMany</c> and returns the counts the server reports.</summary>
    public async Task<VelQueryMongoUpdateResult> UpdateAsync<TArgs>(MongoUpdateDescriptor<TArgs> command, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operation = Begin(command, OperationKind.Write, cancellationToken);
        try
        {
            var collection = _dataSource.GetCollection(command.CollectionName);
            EnsureCountsReported(collection, operation.Session, command.QueryId);
            var filter = new BsonDocumentFilterDefinition<BsonDocument>(command.Filter(in args));
            var update = new BsonDocumentUpdateDefinition<BsonDocument>(command.Update(in args));
            var options = new UpdateOptions { IsUpsert = command.Upsert };
            var session = operation.Session;
            var result = (command.Many, session is null) switch
            {
                (true, true) => await collection.UpdateManyAsync(filter, update, options, operation.Token).ConfigureAwait(false),
                (true, false) => await collection.UpdateManyAsync(session!, filter, update, options, operation.Token).ConfigureAwait(false),
                (false, true) => await collection.UpdateOneAsync(filter, update, options, operation.Token).ConfigureAwait(false),
                (false, false) => await collection.UpdateOneAsync(session!, filter, update, options, operation.Token).ConfigureAwait(false),
            };
            return new VelQueryMongoUpdateResult(result.MatchedCount, result.ModifiedCount, result.UpsertedId);
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

    /// <summary>Runs <c>deleteOne</c>/<c>deleteMany</c> and returns the deleted count the server reports.</summary>
    public async Task<long> DeleteAsync<TArgs>(MongoDeleteDescriptor<TArgs> command, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operation = Begin(command, OperationKind.Write, cancellationToken);
        try
        {
            var collection = _dataSource.GetCollection(command.CollectionName);
            EnsureCountsReported(collection, operation.Session, command.QueryId);
            var filter = new BsonDocumentFilterDefinition<BsonDocument>(command.Filter(in args));
            var session = operation.Session;
            var result = (command.Many, session is null) switch
            {
                (true, true) => await collection.DeleteManyAsync(filter, operation.Token).ConfigureAwait(false),
                (true, false) => await collection.DeleteManyAsync(session!, filter, null, operation.Token).ConfigureAwait(false),
                (false, true) => await collection.DeleteOneAsync(filter, operation.Token).ConfigureAwait(false),
                (false, false) => await collection.DeleteOneAsync(session!, filter, null, operation.Token).ConfigureAwait(false),
            };
            return result.DeletedCount;
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

    /// <summary>
    /// Rejects count-returning writes under an unacknowledged write concern (<c>w: 0</c>) before anything is sent: the
    /// server would report no counts, and VelQuery never invents them (AGENTS rule 3).
    /// </summary>
    private static void EnsureCountsReported(IMongoCollection<BsonDocument> collection, IClientSessionHandle? session, string queryId)
    {
        if (session is { IsInTransaction: true })
        {
            return; // Operations in a transaction are acknowledged; the transaction's write concern applies at commit.
        }

        if (!collection.Settings.WriteConcern.IsAcknowledged)
        {
            throw new VelQueryUnsupportedCapabilityException(queryId,
                "the write concern is unacknowledged (w: 0), so the server reports no counts; use an acknowledged write concern.");
        }
    }

    private static IEnumerable<WriteModel<BsonDocument>> Models<TDocument>(IEnumerator<TDocument> started, IBsonSerializer<TDocument> serializer, string parameterName)
    {
        do
        {
            var document = started.Current;
            if (document is null)
            {
                #pragma warning disable CA2208 // Reports the public method's parameter, not this iterator's.
                throw new ArgumentException("The documents to insert must not contain null.", parameterName);
#pragma warning restore CA2208
            }

            yield return new InsertOneModel<BsonDocument>(new BsonDocumentWrapper(document, serializer));
        }
        while (started.MoveNext());
    }
}
