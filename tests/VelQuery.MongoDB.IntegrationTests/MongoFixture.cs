using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using VelQuery.MongoDB.IntegrationTests;
using Xunit;

[assembly: AssemblyFixture(typeof(MongoFixture))]

namespace VelQuery.MongoDB.IntegrationTests;

/// <summary>
/// One MongoDB instance per test run (xUnit v3 assembly fixture): the official multi-arch <c>mongo:8</c> image as a
/// single-node replica set, so multi-document transactions work, unless <c>VELQUERY_TEST_MONGO</c> supplies a
/// connection string to an existing replica set.
/// </summary>
public sealed class MongoFixture : IAsyncLifetime
{
    public const string ConnectionStringVariable = "VELQUERY_TEST_MONGO";
    public const string ImageVariable = "VELQUERY_TEST_MONGO_IMAGE";
    public const string DefaultImage = "mongo:8";
    public const string Database = "velquery_it";
    public const int PeopleCount = 100;
    public const int BigCount = 100_000;

    private MongoDbContainer? _container;

    public string ConnectionString { get; private set; } = string.Empty;

    public string Source { get; private set; } = string.Empty;

    public TimeSpan StartupTime { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        var external = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            ConnectionString = external;
            Source = "external";
        }
        else
        {
            var image = Environment.GetEnvironmentVariable(ImageVariable);
            Source = string.IsNullOrWhiteSpace(image) ? DefaultImage : image;
            _container = new MongoDbBuilder(Source).WithReplicaSet("rs0").Build();
            await _container.StartAsync(TestContext.Current.CancellationToken);
            ConnectionString = _container.GetConnectionString();
        }

        using var client = new MongoClient(ConnectionString);
        var database = client.GetDatabase(Database);
        await client.DropDatabaseAsync(Database);

        var people = database.GetCollection<BsonDocument>("people");
        await people.InsertManyAsync(Enumerable.Range(1, PeopleCount).Select(i => new BsonDocument
        {
            { "_id", i },
            { "Name", "person-" + i },
            { "Age", 18 + (i % 50) },
            { "Status", i % 3 == 0 ? "inactive" : "active" },
            { "Secret", "s3cret-" + i },
        }));

        var big = database.GetCollection<BsonDocument>("big");
        var payload = new string('x', 150);
        await big.InsertManyAsync(Enumerable.Range(1, BigCount).Select(i => new BsonDocument
        {
            { "_id", i },
            { "Name", "name-" + i },
            { "Payload", payload },
        }));

        StartupTime = stopwatch.Elapsed;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.StopAsync();
            await _container.DisposeAsync();
        }
    }

    /// <summary>A data source that owns a client built from the test connection string (driver retries off).</summary>
    public VelQueryMongoDataSource CreateDataSource(Action<MongoClientSettings>? configure = null, VelQueryMongoOptions? options = null)
    {
        var settings = MongoClientSettings.FromConnectionString(ConnectionString);
        configure?.Invoke(settings);
        return VelQueryMongoDataSource.Create(settings, Database, options);
    }

    /// <summary>A raw driver collection for arranging and asserting test data.</summary>
    public IMongoCollection<BsonDocument> Raw(string collection)
        => new MongoClient(ConnectionString).GetDatabase(Database).GetCollection<BsonDocument>(collection);
}

/// <summary>Base for tests sharing the assembly-wide MongoDB fixture.</summary>
public abstract class IntegrationTest(MongoFixture fixture)
{
    private static VelQueryMongoDataSource? s_shared;

    protected MongoFixture Fixture { get; } = fixture;

    protected static System.Threading.CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A shared data source (one client for the test run, like an application would hold).</summary>
    protected VelQueryMongoDataSource DataSource => s_shared ??= Fixture.CreateDataSource();

    protected VelQueryMongoSession Session() => DataSource.CreateSession();

    /// <summary>Creates an empty, uniquely named collection and returns its name.</summary>
    protected async Task<string> NewCollectionAsync(string prefix)
    {
        var name = prefix + "_" + Guid.NewGuid().ToString("N")[..12];
        await Fixture.Raw(name).Database.CreateCollectionAsync(name, cancellationToken: Ct);
        return name;
    }
}
