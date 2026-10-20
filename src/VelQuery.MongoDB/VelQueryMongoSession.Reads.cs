using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using VelQuery.MongoDB.Execution;

namespace VelQuery.MongoDB;

public sealed partial class VelQueryMongoSession
{
    /// <summary>Runs a find and buffers every document.</summary>
    public Task<IReadOnlyList<TDocument>> FindAsync<TArgs, TDocument>(MongoFindDescriptor<TArgs, TDocument> query, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ReadListAsync(query, new FindOpener<TArgs, TDocument>(this, query, args, ReadMode.Many), cancellationToken);
    }

    /// <summary>Returns the first document; none throws <see cref="VelQueryNoRowsException"/>. Sends <c>limit: 1</c>.</summary>
    public async Task<TDocument> FindFirstAsync<TArgs, TDocument>(MongoFindDescriptor<TArgs, TDocument> query, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (_, document) = await ReadOneAsync(query, new FindOpener<TArgs, TDocument>(this, query, args, ReadMode.First), ReadMode.First, cancellationToken).ConfigureAwait(false);
        return document;
    }

    /// <summary>Returns the first document or <see langword="default"/>. Sends <c>limit: 1</c>.</summary>
    public async Task<TDocument?> FindFirstOrDefaultAsync<TArgs, TDocument>(MongoFindDescriptor<TArgs, TDocument> query, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (found, document) = await ReadOneAsync(query, new FindOpener<TArgs, TDocument>(this, query, args, ReadMode.FirstOrDefault), ReadMode.FirstOrDefault, cancellationToken).ConfigureAwait(false);
        return found ? document : default;
    }

    /// <summary>
    /// Returns the only document; none or more than one throws. Sends <c>limit: 2</c> so a second match is detected
    /// without reading further (unless the contract's own limit is 1).
    /// </summary>
    public async Task<TDocument> FindSingleAsync<TArgs, TDocument>(MongoFindDescriptor<TArgs, TDocument> query, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (_, document) = await ReadOneAsync(query, new FindOpener<TArgs, TDocument>(this, query, args, ReadMode.Single), ReadMode.Single, cancellationToken).ConfigureAwait(false);
        return document;
    }

    /// <summary>Returns the only document or <see langword="default"/>; more than one throws <see cref="VelQueryTooManyRowsException"/>.</summary>
    public async Task<TDocument?> FindSingleOrDefaultAsync<TArgs, TDocument>(MongoFindDescriptor<TArgs, TDocument> query, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (found, document) = await ReadOneAsync(query, new FindOpener<TArgs, TDocument>(this, query, args, ReadMode.SingleOrDefault), ReadMode.SingleOrDefault, cancellationToken).ConfigureAwait(false);
        return found ? document : default;
    }

    /// <summary>
    /// Streams the documents of a find. The query runs when enumeration starts; the cursor is owned by the enumerator and
    /// killed on the server when enumeration stops early, fails or is cancelled.
    /// </summary>
    public IAsyncEnumerable<TDocument> FindStreamAsync<TArgs, TDocument>(MongoFindDescriptor<TArgs, TDocument> query, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfDisposed();
        return new MongoCursorStream<TDocument>(this, query.QueryId, new FindOpener<TArgs, TDocument>(this, query, args, ReadMode.Many), cancellationToken);
    }

    /// <summary>Counts matching documents (<c>countDocuments</c>, an exact count).</summary>
    public async Task<long> CountAsync<TArgs>(MongoCountDescriptor<TArgs> query, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var operation = Begin(query, OperationKind.Read, cancellationToken);
        try
        {
            var collection = _dataSource.GetCollection(query.CollectionName);
            var filter = new BsonDocumentFilterDefinition<BsonDocument>(query.Filter(in args));
            var options = new CountOptions { Limit = query.Limit, Skip = query.Skip, MaxTime = operation.MaxTime };
            return operation.Session is null
                ? await collection.CountDocumentsAsync(filter, options, operation.Token).ConfigureAwait(false)
                : await collection.CountDocumentsAsync(operation.Session, filter, options, operation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Translate(ex, query.QueryId, operation, cancellationToken);
        }
        finally
        {
            operation.Dispose();
        }
    }

    /// <summary>Runs an aggregation and buffers every output document.</summary>
    public Task<IReadOnlyList<TResult>> AggregateAsync<TArgs, TResult>(MongoAggregateDescriptor<TArgs, TResult> query, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ReadListAsync(query, new AggregateOpener<TArgs, TResult>(this, query, args, ReadMode.Many), cancellationToken);
    }

    /// <summary>Returns the first output document; none throws. Appends <c>{ $limit: 1 }</c>.</summary>
    public async Task<TResult> AggregateFirstAsync<TArgs, TResult>(MongoAggregateDescriptor<TArgs, TResult> query, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (_, result) = await ReadOneAsync(query, new AggregateOpener<TArgs, TResult>(this, query, args, ReadMode.First), ReadMode.First, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Returns the first output document or <see langword="default"/>. Appends <c>{ $limit: 1 }</c>.</summary>
    public async Task<TResult?> AggregateFirstOrDefaultAsync<TArgs, TResult>(MongoAggregateDescriptor<TArgs, TResult> query, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (found, result) = await ReadOneAsync(query, new AggregateOpener<TArgs, TResult>(this, query, args, ReadMode.FirstOrDefault), ReadMode.FirstOrDefault, cancellationToken).ConfigureAwait(false);
        return found ? result : default;
    }

    /// <summary>Returns the only output document; none or more than one throws. Appends <c>{ $limit: 2 }</c>.</summary>
    public async Task<TResult> AggregateSingleAsync<TArgs, TResult>(MongoAggregateDescriptor<TArgs, TResult> query, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (_, result) = await ReadOneAsync(query, new AggregateOpener<TArgs, TResult>(this, query, args, ReadMode.Single), ReadMode.Single, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Returns the only output document or <see langword="default"/>; more than one throws. Appends <c>{ $limit: 2 }</c>.</summary>
    public async Task<TResult?> AggregateSingleOrDefaultAsync<TArgs, TResult>(MongoAggregateDescriptor<TArgs, TResult> query, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (found, result) = await ReadOneAsync(query, new AggregateOpener<TArgs, TResult>(this, query, args, ReadMode.SingleOrDefault), ReadMode.SingleOrDefault, cancellationToken).ConfigureAwait(false);
        return found ? result : default;
    }

    /// <summary>Streams the output of an aggregation; see <see cref="FindStreamAsync"/> for cursor ownership.</summary>
    public IAsyncEnumerable<TResult> AggregateStreamAsync<TArgs, TResult>(MongoAggregateDescriptor<TArgs, TResult> query, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfDisposed();
        return new MongoCursorStream<TResult>(this, query.QueryId, new AggregateOpener<TArgs, TResult>(this, query, args, ReadMode.Many), cancellationToken);
    }

    private async Task<IReadOnlyList<T>> ReadListAsync<T>(MongoOperationDescriptor query, CursorOpener<T> opener, CancellationToken cancellationToken)
    {
        var operation = Begin(query, OperationKind.Read, cancellationToken);
        IAsyncCursor<T>? cursor = null;
        try
        {
            cursor = await opener.OpenAsync(operation.Session, operation.MaxTime, operation.Token).ConfigureAwait(false);
            var list = new List<T>();
            while (await cursor.MoveNextAsync(operation.Token).ConfigureAwait(false))
            {
                list.AddRange(cursor.Current);
            }

            return list;
        }
        catch (Exception ex)
        {
            throw Translate(ex, query.QueryId, operation, cancellationToken);
        }
        finally
        {
            // AsyncCursor.Dispose kills a still-open server cursor and never throws, so it cannot hide a failure.
            cursor?.Dispose();
            operation.Dispose();
        }
    }

    private async Task<(bool Found, T Item)> ReadOneAsync<T>(MongoOperationDescriptor query, CursorOpener<T> opener, ReadMode mode, CancellationToken cancellationToken)
    {
        var operation = Begin(query, OperationKind.Read, cancellationToken);
        IAsyncCursor<T>? cursor = null;
        try
        {
            cursor = await opener.OpenAsync(operation.Session, operation.MaxTime, operation.Token).ConfigureAwait(false);
            var found = false;
            T first = default!;
            while (await cursor.MoveNextAsync(operation.Token).ConfigureAwait(false))
            {
                foreach (var item in cursor.Current)
                {
                    if (found)
                    {
                        throw new VelQueryTooManyRowsException(query.QueryId);
                    }

                    found = true;
                    first = item;
                    if (mode is ReadMode.First or ReadMode.FirstOrDefault)
                    {
                        return (true, first);
                    }
                }
            }

            if (!found && mode is ReadMode.First or ReadMode.Single)
            {
                throw new VelQueryNoRowsException(query.QueryId);
            }

            return (found, first);
        }
        catch (Exception ex)
        {
            throw Translate(ex, query.QueryId, operation, cancellationToken);
        }
        finally
        {
            cursor?.Dispose();
            operation.Dispose();
        }
    }

    internal StreamLease BeginStream(MongoOperationDescriptor query, CancellationToken cancellationToken)
    {
        var operation = Begin(query, OperationKind.Stream, cancellationToken);
        return new StreamLease(operation.Session, operation.MaxTime, operation.Dispose);
    }

    internal readonly record struct StreamLease(IClientSessionHandle? Session, TimeSpan? MaxTime, Action Release);

    private static int? EffectiveLimit(int? limit, ReadMode mode)
    {
        var declared = limit is > 0 ? limit : null;
        return mode switch
        {
            ReadMode.First or ReadMode.FirstOrDefault => 1,
            ReadMode.Single or ReadMode.SingleOrDefault => declared == 1 ? 1 : 2,
            _ => declared,
        };
    }

    internal enum ReadMode
    {
        Many,
        First,
        FirstOrDefault,
        Single,
        SingleOrDefault,
    }

    /// <summary>Opens a cursor for one execution; also used by streams, which open on first enumeration.</summary>
    internal abstract class CursorOpener<T>
    {
        public abstract string QueryId { get; }

        public abstract MongoOperationDescriptor Descriptor { get; }

        public abstract Task<IAsyncCursor<T>> OpenAsync(IClientSessionHandle? session, TimeSpan? maxTime, CancellationToken cancellationToken);
    }

    private sealed class FindOpener<TArgs, TDocument>(VelQueryMongoSession owner, MongoFindDescriptor<TArgs, TDocument> query, TArgs args, ReadMode mode)
        : CursorOpener<TDocument>
    {
        public override string QueryId => query.QueryId;

        public override MongoOperationDescriptor Descriptor => query;

        public override Task<IAsyncCursor<TDocument>> OpenAsync(IClientSessionHandle? session, TimeSpan? maxTime, CancellationToken cancellationToken)
        {
            var collection = owner._dataSource.GetCollection(query.CollectionName);
            var filter = new BsonDocumentFilterDefinition<BsonDocument>(query.Filter(in args));
            var options = new FindOptions<BsonDocument, TDocument>
            {
                Projection = new SerializerProjection<TDocument>(query.Projection?.Invoke(), query.Serializer),
                Sort = query.Sort is { } sort ? new BsonDocumentSortDefinition<BsonDocument>(sort()) : null,
                Limit = EffectiveLimit(query.Limit, mode),
                Skip = query.Skip is > 0 ? query.Skip : null,
                MaxTime = maxTime,
            };
            return session is null
                ? collection.FindAsync(filter, options, cancellationToken)
                : collection.FindAsync(session, filter, options, cancellationToken);
        }
    }

    private sealed class AggregateOpener<TArgs, TResult>(VelQueryMongoSession owner, MongoAggregateDescriptor<TArgs, TResult> query, TArgs args, ReadMode mode)
        : CursorOpener<TResult>
    {
        public override string QueryId => query.QueryId;

        public override MongoOperationDescriptor Descriptor => query;

        public override Task<IAsyncCursor<TResult>> OpenAsync(IClientSessionHandle? session, TimeSpan? maxTime, CancellationToken cancellationToken)
        {
            var collection = owner._dataSource.GetCollection(query.CollectionName);
            var stages = query.Pipeline(in args);
            if (EffectiveLimit(null, mode) is { } limit)
            {
                // Truncating the output is always semantics-preserving for First/Single and saves server work.
                Array.Resize(ref stages, stages.Length + 1);
                stages[^1] = new BsonDocument("$limit", limit);
            }

            var pipeline = PipelineDefinition<BsonDocument, TResult>.Create(stages, query.Serializer);
            var options = new AggregateOptions { AllowDiskUse = query.AllowDiskUse ? true : null, MaxTime = maxTime };
            return session is null
                ? collection.AggregateAsync(pipeline, options, cancellationToken)
                : collection.AggregateAsync(session, pipeline, options, cancellationToken);
        }
    }
}
