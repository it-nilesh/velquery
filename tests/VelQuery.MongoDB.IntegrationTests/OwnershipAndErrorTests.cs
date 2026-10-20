using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using VelQuery.Providers;
using Xunit;

namespace VelQuery.MongoDB.IntegrationTests;

/// <summary>Client ownership, registry-free mapping, retry settings and the error classifier.</summary>
public sealed class OwnershipAndErrorTests(MongoFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task No_class_map_or_registry_entry_is_created_for_document_types()
    {
        await using var session = Session();
        _ = await PeopleByStatus.ExecuteAsync(session, "active", 0, Ct);
        _ = await CountsByStatus.ExecuteAsync(session, Ct);
        await Fixture.Raw("accounts").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);
        await InsertAccounts.ExecuteAsync(session, [new Account(1, "a", 1m)], Ct);
        await InsertAccount.ExecuteAsync(session, new Account(2, "b", 2m), Ct);
        _ = await AllAccounts.ExecuteAsync(session, Ct);

        foreach (var type in new[] { typeof(Person), typeof(StatusCount), typeof(Account), typeof(TypeDoc), typeof(Address) })
        {
            Assert.False(BsonClassMap.IsClassMapRegistered(type), $"{type.Name} got a reflection class map");
        }
    }

    [Fact]
    public async Task Application_owned_client_is_never_disposed()
    {
        using var client = new MongoClient(Fixture.ConnectionString);
        var source = new VelQueryMongoDataSource(client, MongoFixture.Database);
        await using (var session = source.CreateSession())
        {
            Assert.Equal(33, await CountByStatus.ExecuteAsync(session, "inactive", Ct));
        }

        source.Dispose();
        Assert.Equal(33, await client.GetDatabase(MongoFixture.Database).GetCollection<BsonDocument>("people")
            .CountDocumentsAsync(new BsonDocument("Status", "inactive"), cancellationToken: Ct));
        Assert.Throws<ObjectDisposedException>(() => source.CreateSession());
    }

    [Fact]
    public async Task Owned_client_is_disposed_with_the_data_source()
    {
        var source = VelQueryMongoDataSource.Create(Fixture.ConnectionString, MongoFixture.Database);
        var session = source.CreateSession();
        Assert.Equal(33, await CountByStatus.ExecuteAsync(session, "inactive", Ct));
        source.Dispose();
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => CountByStatus.ExecuteAsync(session, "inactive", Ct));
    }

    [Fact]
    public void Clients_created_by_velquery_do_not_retry_implicitly_unless_the_connection_string_says_so()
    {
        using var implicitRetries = VelQueryMongoDataSource.Create(Fixture.ConnectionString, MongoFixture.Database);
        var settings = implicitRetries.Client.Settings;
        Assert.False(settings.RetryReads);
        Assert.False(settings.RetryWrites);
        Assert.Equal(0, settings.MaxAdaptiveRetries);

        var separator = Fixture.ConnectionString.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        using var explicitRetries = VelQueryMongoDataSource.Create(Fixture.ConnectionString + separator + "retryWrites=true", MongoFixture.Database);
        Assert.True(explicitRetries.Client.Settings.RetryWrites);
        Assert.False(explicitRetries.Client.Settings.RetryReads);

        using var fromSettings = VelQueryMongoDataSource.Create(MongoClientSettings.FromConnectionString(Fixture.ConnectionString + separator + "retryWrites=true"), MongoFixture.Database);
        Assert.False(fromSettings.Client.Settings.RetryWrites);
    }

    [Fact]
    public void Invalid_connection_strings_do_not_echo_the_string()
    {
        const string bad = "mongodb://user:hunter2@:::not-a-host";
        var error = Assert.Throws<VelQueryValidationException>(() => VelQueryMongoDataSource.Create(bad, "db"));
        Assert.DoesNotContain("hunter2", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Dependency_injection_registers_a_singleton_source_and_scoped_sessions()
    {
        var services = new ServiceCollection();
        services.AddVelQueryMongo(Fixture.ConnectionString, MongoFixture.Database, o => o.DefaultTimeout = TimeSpan.FromSeconds(30));
        await using var provider = services.BuildServiceProvider();
        VelQueryMongoSession first;
        await using (var scope = provider.CreateAsyncScope())
        {
            first = scope.ServiceProvider.GetRequiredService<VelQueryMongoSession>();
            Assert.Same(first, scope.ServiceProvider.GetRequiredService<VelQueryMongoSession>());
            Assert.Equal(33, await CountByStatus.ExecuteAsync(first, "inactive", Ct));
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => CountByStatus.ExecuteAsync(first, "inactive", Ct));
        Assert.Same(provider.GetRequiredService<VelQueryMongoDataSource>(), provider.GetRequiredService<VelQueryMongoDataSource>());
    }

    [Fact]
    public void Classifier_maps_driver_failures_to_the_error_contract()
    {
        var classifier = MongoErrorClassifier.Instance;
        Assert.Equal(ProviderErrorKind.Timeout, classifier.Classify(new TimeoutException("server selection")));
        Assert.Equal(ProviderErrorKind.Cancelled, classifier.Classify(new OperationCanceledException()));
        Assert.Equal(ProviderErrorKind.Other, classifier.Classify(new MongoClientException("x")));
        Assert.True(classifier.IsProviderException(new MongoClientException("x")));
        Assert.True(classifier.IsProviderException(new BsonSerializationException("x")));
        Assert.False(classifier.IsProviderException(new InvalidOperationException("caller code")));
    }

    [Fact]
    public async Task Server_selection_timeout_is_a_velquery_timeout()
    {
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:1/?directConnection=true");
        settings.ServerSelectionTimeout = TimeSpan.FromMilliseconds(300);
        settings.ConnectTimeout = TimeSpan.FromMilliseconds(300);
        using var source = VelQueryMongoDataSource.Create(settings, "db");
        await using var session = source.CreateSession();
        var error = await Assert.ThrowsAsync<VelQueryTimeoutException>(() => CountByStatus.ExecuteAsync(session, "x", Ct));
        Assert.Equal("people.countByStatus", error.QueryId);
        Assert.IsType<TimeoutException>(error.InnerException);
    }

    [Fact]
    public async Task Disposed_session_rejects_operations()
    {
        var session = Session();
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => CountByStatus.ExecuteAsync(session, "x", Ct));
        Assert.Throws<ObjectDisposedException>(() => StreamBig.ExecuteAsync(session, CancellationToken.None));
    }
}
