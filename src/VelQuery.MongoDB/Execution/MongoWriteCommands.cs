using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace VelQuery.MongoDB;

/// <summary>
/// Issues <c>insert</c>, <c>update</c> and <c>delete</c> as raw server commands (ADR-0019 revision).
/// <para>The driver's collection write API offers no server-side time limit and wraps every document twice when a
/// caller supplies its own serializer. Commands built here carry <c>maxTimeMS</c>, so the server enforces the
/// operation timeout; and bulk inserts are framed by hand straight into the driver's pooled chunk buffers with the
/// generated serializer, with no per-document wrapper or write-model objects. Measured on 1M documents: 1.18 s,
/// 0.42 s CPU and 167 MB allocated, versus 1.94 s, 1.00 s and 505 MB through the driver's typed <c>InsertMany</c>.</para>
/// <para>The commands run through <see cref="IMongoDatabase.RunCommandAsync{TResult}(IClientSessionHandle, Command{TResult}, ReadPreference, CancellationToken)"/>,
/// so a session's transaction applies; they are never retried (AGENTS rule 3).</para>
/// </summary>
internal static class MongoWriteCommands
{
    // Commands may be up to maxBsonObjectSize (16 MiB) plus 16 KiB; documents stop short of it, leaving room for the
    // trailing fields (ordered, maxTimeMS, writeConcern).
    private const int MaxBatchBytes = (16 * 1024 * 1024) - (64 * 1024);

    // The server's maxWriteBatchSize.
    private const int MaxBatchCount = 100_000;

    /// <summary>
    /// Inserts every document of <paramref name="started"/> (positioned on its first element), in ordered batches.
    /// Returns the inserted count the server reports.
    /// </summary>
    public static async Task<long> InsertAsync<TDocument>(IMongoDatabase database, string collection, IEnumerator<TDocument> started,
        IBsonSerializer<TDocument> serializer, WriteLimits limits, string parameterName, CancellationToken cancellationToken)
    {
        long inserted = 0;
        var pending = true;
        while (pending)
        {
            var buffer = new MultiChunkBuffer(BsonChunkPool.Default);
            RawBsonDocument? command = null;
            int count;
            try
            {
                using (var stream = new ByteBufferStream(buffer, ownsBuffer: false))
                using (var writer = new BsonBinaryWriter(stream))
                {
                    var context = BsonSerializationContext.CreateRoot(writer);
                    stream.WriteInt32(0);
                    stream.WriteBsonType(BsonType.String);
                    stream.WriteCString("insert");
                    stream.WriteString(collection, Utf8Encodings.Strict);
                    stream.WriteBsonType(BsonType.Array);
                    stream.WriteCString("documents");
                    var arrayStart = stream.Position;
                    stream.WriteInt32(0);
                    count = 0;
                    while (pending)
                    {
                        var document = started.Current;
                        if (document is null)
                        {
#pragma warning disable CA2208 // Reports the public method's parameter.
                            throw new ArgumentException("The documents to insert must not contain null.", parameterName);
#pragma warning restore CA2208
                        }

                        var elementStart = stream.Position;
                        stream.WriteBsonType(BsonType.Document);
                        WriteIndex(stream, count);
                        serializer.Serialize(context, document);
                        if (stream.Position > MaxBatchBytes && count > 0)
                        {
                            // Too large for this command: drop the element and send it first in the next batch.
                            stream.Position = elementStart;
                            stream.SetLength(elementStart);
                            break;
                        }

                        count++;
                        pending = started.MoveNext();
                        if (count == MaxBatchCount)
                        {
                            break;
                        }
                    }

                    stream.WriteByte(0);
                    PatchLength(stream, arrayStart);
                    stream.WriteBsonType(BsonType.Boolean);
                    stream.WriteCString("ordered");
                    stream.WriteBoolean(true);
                    WriteLimitFields(stream, context, limits);
                    stream.WriteByte(0);
                    PatchLength(stream, 0);
                    buffer.Length = (int)stream.Length;
                }

                buffer.MakeReadOnly();
                command = new RawBsonDocument(buffer);
                buffer = null!; // Owned by the RawBsonDocument now.
                var reply = await RunAsync(database, limits.Session, command, cancellationToken).ConfigureAwait(false);
                var n = Count(reply, "n");
                ThrowOnErrors("insert", reply, inserted + n, indexOffset: inserted);
                inserted += n;
            }
            finally
            {
                command?.Dispose();
                buffer?.Dispose();
            }
        }

        return inserted;
    }

    /// <summary>Runs one <c>update</c> statement and returns matched/modified counts and the upserted id.</summary>
    public static async Task<VelQueryMongoUpdateResult> UpdateAsync(IMongoDatabase database, string collection, BsonDocument filter, BsonDocument update,
        bool many, bool upsert, WriteLimits limits, CancellationToken cancellationToken)
    {
        var statement = new BsonDocument { { "q", filter }, { "u", update }, { "multi", many }, { "upsert", upsert } };
        var command = new BsonDocument { { "update", collection }, { "updates", new BsonArray { statement } }, { "ordered", true } };
        AddLimitFields(command, limits);
        var reply = await RunAsync(database, limits.Session, command, cancellationToken).ConfigureAwait(false);
        var n = Count(reply, "n");
        BsonValue? upsertedId = null;
        long upserted = 0;
        if (reply.TryGetValue("upserted", out var list) && list is BsonArray { Count: > 0 } entries)
        {
            upserted = entries.Count;
            upsertedId = entries[0]["_id"];
        }

        // The server's n counts upserted documents too; MatchedCount follows the driver's definition.
        ThrowOnErrors("update", reply, n - upserted, indexOffset: 0);
        return new VelQueryMongoUpdateResult(n - upserted, Count(reply, "nModified"), upsertedId);
    }

    /// <summary>Runs one <c>delete</c> statement and returns the deleted count.</summary>
    public static async Task<long> DeleteAsync(IMongoDatabase database, string collection, BsonDocument filter, bool many,
        WriteLimits limits, CancellationToken cancellationToken)
    {
        var statement = new BsonDocument { { "q", filter }, { "limit", many ? 0 : 1 } };
        var command = new BsonDocument { { "delete", collection }, { "deletes", new BsonArray { statement } }, { "ordered", true } };
        AddLimitFields(command, limits);
        var reply = await RunAsync(database, limits.Session, command, cancellationToken).ConfigureAwait(false);
        var n = Count(reply, "n");
        ThrowOnErrors("delete", reply, n, indexOffset: 0);
        return n;
    }

    private static Task<BsonDocument> RunAsync(IMongoDatabase database, IClientSessionHandle? session, BsonDocument command, CancellationToken cancellationToken)
    {
        var typed = new BsonDocumentCommand<BsonDocument>(command);
        return session is null
            ? database.RunCommandAsync(typed, ReadPreference.Primary, cancellationToken)
            : database.RunCommandAsync(session, typed, ReadPreference.Primary, cancellationToken);
    }

    private static long Count(BsonDocument reply, string name)
        => reply.TryGetValue(name, out var value) && value.IsNumeric ? value.ToInt64() : 0;

    private static void ThrowOnErrors(string commandName, BsonDocument reply, long count, long indexOffset)
    {
        List<MongoWriteCommandError>? errors = null;
        if (reply.TryGetValue("writeErrors", out var list) && list is BsonArray entries)
        {
            foreach (var entry in entries)
            {
                errors ??= [];
                errors.Add(Error(entry.AsBsonDocument, indexOffset));
            }
        }

        MongoWriteCommandError? concern = null;
        if (reply.TryGetValue("writeConcernError", out var wce) && wce is BsonDocument concernDocument)
        {
            concern = Error(concernDocument, 0);
        }

        if (errors is not null || concern is not null)
        {
            throw new MongoWriteCommandException(commandName, count, (IReadOnlyList<MongoWriteCommandError>?)errors ?? [], concern);
        }
    }

    private static MongoWriteCommandError Error(BsonDocument entry, long indexOffset)
        => new(
            indexOffset + (entry.TryGetValue("index", out var index) && index.IsNumeric ? index.ToInt64() : 0),
            entry.TryGetValue("code", out var code) && code.IsNumeric ? code.ToInt32() : 0,
            entry.TryGetValue("codeName", out var name) && name.IsString ? name.AsString : null,
            entry.TryGetValue("errmsg", out var message) && message.IsString ? message.AsString : string.Empty);

    private static void AddLimitFields(BsonDocument command, WriteLimits limits)
    {
        if (limits.MaxTimeMilliseconds is { } milliseconds)
        {
            command.Add("maxTimeMS", milliseconds);
        }

        if (limits.WriteConcern is { } writeConcern)
        {
            command.Add("writeConcern", writeConcern);
        }
    }

    private static void WriteLimitFields(BsonStream stream, BsonSerializationContext context, WriteLimits limits)
    {
        if (limits.MaxTimeMilliseconds is { } milliseconds)
        {
            stream.WriteBsonType(BsonType.Int32);
            stream.WriteCString("maxTimeMS");
            stream.WriteInt32(milliseconds);
        }

        if (limits.WriteConcern is { } writeConcern)
        {
            stream.WriteBsonType(BsonType.Document);
            stream.WriteCString("writeConcern");
            BsonDocumentSerializer.Instance.Serialize(context, writeConcern);
        }
    }

    /// <summary>Writes an array element key ("0", "1", …) without allocating a string.</summary>
    private static void WriteIndex(BsonStream stream, int index)
    {
        Span<byte> digits = stackalloc byte[10];
        var position = digits.Length;
        do
        {
            digits[--position] = (byte)('0' + (index % 10));
            index /= 10;
        }
        while (index > 0);

        for (var i = position; i < digits.Length; i++)
        {
            stream.WriteByte(digits[i]);
        }

        stream.WriteByte(0);
    }

    private static void PatchLength(BsonStream stream, long start)
    {
        var end = stream.Position;
        stream.Position = start;
        stream.WriteInt32((int)(end - start));
        stream.Position = end;
    }
}

/// <summary>
/// Per-operation write settings: the driver session (transactions), the server time limit, and the write concern to
/// send. Inside a transaction no write concern is sent (the transaction's applies at commit).
/// </summary>
internal readonly record struct WriteLimits(IClientSessionHandle? Session, int? MaxTimeMilliseconds, BsonDocument? WriteConcern)
{
    public static WriteLimits For(IClientSessionHandle? session, TimeSpan? maxTime, WriteConcern writeConcern)
        => new(
            session,
            maxTime is { } limit ? (int)Math.Clamp(Math.Ceiling(limit.TotalMilliseconds), 1, int.MaxValue) : null,
            session is { IsInTransaction: true } || writeConcern.IsServerDefault ? null : writeConcern.ToBsonDocument());
}
