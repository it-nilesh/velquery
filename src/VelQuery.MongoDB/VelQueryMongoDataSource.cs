using System;
using System.Collections.Concurrent;
using System.Threading;
using MongoDB.Bson;
using MongoDB.Driver;

namespace VelQuery.MongoDB;

/// <summary>
/// Entry point for MongoDB: a client plus database name. Ownership is explicit (VQ-FR-011, ADR-0002): a data source
/// created by <see cref="Create(string, string?, VelQueryMongoOptions?)"/> owns its <see cref="MongoClient"/> and
/// disposes it; one constructed over an application-owned <see cref="IMongoClient"/> never disposes it.
/// </summary>
/// <remarks>
/// <para><b>No implicit retries (AGENTS rule 3).</b> Clients VelQuery creates have retryable reads, retryable writes
/// and adaptive overload retries turned off unless the connection string turns them on explicitly. An application-owned
/// client keeps the application's settings (MongoDB.Driver enables retryable reads and writes by default). Independent
/// of these settings, the driver retries <c>commitTransaction</c>/<c>abortTransaction</c> once, as the MongoDB
/// transactions specification requires; the server makes these commands idempotent.</para>
/// <para>Inserts are serialized by the generated serializer, so VelQuery never mutates the documents you insert. An
/// <c>ObjectId</c> <c>_id</c> left as <see cref="ObjectId.Empty"/> is omitted and assigned by the server.</para>
/// </remarks>
public sealed class VelQueryMongoDataSource : IDisposable
{
    private readonly ConcurrentDictionary<string, IMongoCollection<BsonDocument>> _collections = new(StringComparer.Ordinal);
    private readonly bool _ownsClient;
    private int _disposed;

    /// <summary>Creates a data source over an application-owned client, which VelQuery never disposes.</summary>
    public VelQueryMongoDataSource(IMongoClient client, string databaseName, VelQueryMongoOptions? options = null)
        : this(client, databaseName, options, ownsClient: false)
    {
    }

    private VelQueryMongoDataSource(IMongoClient client, string databaseName, VelQueryMongoOptions? options, bool ownsClient)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        Client = client;
        DatabaseName = databaseName;
        Database = client.GetDatabase(databaseName);
        Options = (options ?? new VelQueryMongoOptions()).Snapshot();
        _ownsClient = ownsClient;
    }

    /// <summary>The client used for every operation.</summary>
    public IMongoClient Client { get; }

    /// <summary>The database name.</summary>
    public string DatabaseName { get; }

    /// <summary>The database.</summary>
    public IMongoDatabase Database { get; }

    internal VelQueryMongoOptions Options { get; }

    /// <summary>
    /// Creates a data source that owns a new <see cref="MongoClient"/> built from <paramref name="connectionString"/>.
    /// <paramref name="databaseName"/> defaults to the database in the connection string.
    /// </summary>
    public static VelQueryMongoDataSource Create(string connectionString, string? databaseName = null, VelQueryMongoOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        MongoUrl url;
        try
        {
            url = MongoUrl.Create(connectionString);
        }
        catch (MongoConfigurationException)
        {
            // The driver's message can echo the connection string, which may hold credentials (AGENTS rule 5).
            throw new VelQueryValidationException(null, "the MongoDB connection string is not valid.");
        }

        var database = databaseName ?? url.DatabaseName;
        if (string.IsNullOrWhiteSpace(database))
        {
            throw new VelQueryValidationException(null, "no database name was given and the connection string does not name one.");
        }

        var settings = MongoClientSettings.FromUrl(url);
        settings.RetryReads = url.RetryReads ?? false;
        settings.RetryWrites = url.RetryWrites ?? false;
        settings.MaxAdaptiveRetries = url.MaxAdaptiveRetries ?? 0;
        return new VelQueryMongoDataSource(new MongoClient(settings), database, options, ownsClient: true);
    }

    /// <summary>
    /// Creates a data source that owns a new <see cref="MongoClient"/> built from a copy of <paramref name="settings"/>.
    /// Retryable reads/writes and adaptive retries are turned off on the copy; to opt in to driver retries, construct the
    /// data source over your own client instead.
    /// </summary>
    public static VelQueryMongoDataSource Create(MongoClientSettings settings, string databaseName, VelQueryMongoOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = settings.Clone();
        copy.RetryReads = false;
        copy.RetryWrites = false;
        copy.MaxAdaptiveRetries = 0;
        return new VelQueryMongoDataSource(new MongoClient(copy), databaseName, options, ownsClient: true);
    }

    /// <summary>
    /// Creates a session. Operations run without a driver session (one pooled connection per operation) until
    /// <see cref="VelQueryMongoSession.BeginTransactionAsync"/> is called.
    /// </summary>
    public VelQueryMongoSession CreateSession()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return new VelQueryMongoSession(this, null);
    }

    /// <summary>
    /// Creates a session whose operations all run in an application-owned driver session (for example one with a
    /// transaction the application manages). VelQuery never commits, aborts or ends <paramref name="clientSession"/>.
    /// </summary>
    public VelQueryMongoSession CreateSession(IClientSessionHandle clientSession)
    {
        ArgumentNullException.ThrowIfNull(clientSession);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!ReferenceEquals(clientSession.Client, Client))
        {
            throw new VelQueryValidationException(null, "the client session belongs to a different MongoClient than this data source.");
        }

        return new VelQueryMongoSession(this, clientSession);
    }

    /// <summary>Disposes the client if this data source created it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsClient)
        {
            Client.Dispose();
        }
    }

    internal IMongoCollection<BsonDocument> GetCollection(string name)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Collection objects are immutable, thread-safe driver configuration (no connection, command or cursor), so
        // caching them per name is caching immutable data (AGENTS rule 7). Names come from compile-time contracts.
        return _collections.GetOrAdd(name, static (n, database) =>
            database.GetCollection<BsonDocument>(n), Database);
    }
}
