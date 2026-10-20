using System;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace VelQuery.MongoDB.IntegrationTests;

/// <summary>
/// Strict mapping (VQ-FR-016): lossless conversions only, NULL and missing required fields fail, and every error
/// names the query and field but never the value.
/// </summary>
public sealed class StrictMappingTests(MongoFixture fixture) : IntegrationTest(fixture), IAsyncLifetime
{
    private const string SecretText = "do-not-leak-this-value";

    public async ValueTask InitializeAsync()
    {
        var strict = Fixture.Raw("strict");
        await strict.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);
        await strict.InsertManyAsync(
        [
            new BsonDocument { { "_id", 1 }, { "Value", 42L } },
            new BsonDocument { { "_id", 2 }, { "Value", 5_000_000_000L } },
            new BsonDocument { { "_id", 3 }, { "Value", 1.5 } },
            new BsonDocument { { "_id", 4 }, { "Value", BsonNull.Value } },
            new BsonDocument { { "_id", 5 } },
            new BsonDocument { { "_id", 6 }, { "Value", 7 } },
            new BsonDocument { { "_id", 7 }, { "Value", new BsonDecimal128(Decimal128.Parse("1.000000000000000000000000000000001")) } },
            new BsonDocument { { "_id", 8 }, { "Value", SecretText } },
            new BsonDocument { { "_id", 9 }, { "Value", new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.CSharpLegacy) } },
            new BsonDocument { { "_id", 10 }, { "Value", new BsonDecimal128(Decimal128.Parse("12.50")) } },
            new BsonDocument { { "_id", 11 }, { "Value", 9_007_199_254_740_993L } },
        ], cancellationToken: Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Lossless_conversions_succeed()
    {
        await using var session = Session();
        Assert.Equal(42, (await ReadStrictInt.ExecuteAsync(session, 1, Ct)).Value); // Int64 in range -> int
        Assert.Equal(7L, (await ReadStrictLong.ExecuteAsync(session, 6, Ct)).Value); // Int32 -> long
        Assert.Equal(7d, (await ReadStrictDouble.ExecuteAsync(session, 6, Ct)).Value); // Int32 -> double
        Assert.Equal(7m, (await ReadStrictDecimal.ExecuteAsync(session, 6, Ct)).Value); // Int32 -> decimal
        Assert.Equal(12.50m, (await ReadStrictDecimal.ExecuteAsync(session, 10, Ct)).Value); // exact Decimal128 -> decimal
        Assert.Equal(1.5, (await ReadStrictDouble.ExecuteAsync(session, 3, Ct)).Value);
    }

    [Fact]
    public async Task Lossy_conversions_fail_naming_the_field_and_types()
    {
        await using var session = Session();
        var overflow = await Assert.ThrowsAsync<VelQueryMappingException>(() => ReadStrictInt.ExecuteAsync(session, 2, Ct));
        AssertMapping(overflow, "strict.int", "Value", typeof(int));

        var fraction = await Assert.ThrowsAsync<VelQueryMappingException>(() => ReadStrictInt.ExecuteAsync(session, 3, Ct));
        AssertMapping(fraction, "strict.int", "Value", typeof(int));
        Assert.Contains("Double", fraction.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("1.5", fraction.Message, StringComparison.Ordinal);

        var doubleToDecimal = await Assert.ThrowsAsync<VelQueryMappingException>(() => ReadStrictDecimal.ExecuteAsync(session, 3, Ct));
        AssertMapping(doubleToDecimal, "strict.decimal", "Value", typeof(decimal));

        var inexact = await Assert.ThrowsAsync<VelQueryMappingException>(() => ReadStrictDecimal.ExecuteAsync(session, 7, Ct));
        AssertMapping(inexact, "strict.decimal", "Value", typeof(decimal));

        var longToDouble = await Assert.ThrowsAsync<VelQueryMappingException>(() => ReadStrictDouble.ExecuteAsync(session, 11, Ct));
        AssertMapping(longToDouble, "strict.double", "Value", typeof(double));

        var textToInt = await Assert.ThrowsAsync<VelQueryMappingException>(() => ReadStrictInt.ExecuteAsync(session, 8, Ct));
        AssertMapping(textToInt, "strict.int", "Value", typeof(int));
        Assert.DoesNotContain(SecretText, textToInt.ToString(), StringComparison.Ordinal);

        var intToText = await Assert.ThrowsAsync<VelQueryMappingException>(() => ReadStrictText.ExecuteAsync(session, 1, Ct));
        AssertMapping(intToText, "strict.text", "Value", typeof(string));

        var legacyGuid = await Assert.ThrowsAsync<VelQueryMappingException>(() => ReadStrictGuid.ExecuteAsync(session, 9, Ct));
        AssertMapping(legacyGuid, "strict.guid", "Value", typeof(Guid));
        Assert.Contains("UuidLegacy", legacyGuid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Null_and_missing_required_fields_fail()
    {
        await using var session = Session();
        var nullValue = await Assert.ThrowsAsync<VelQueryMappingException>(() => ReadStrictInt.ExecuteAsync(session, 4, Ct));
        AssertMapping(nullValue, "strict.int", "Value", typeof(int));
        Assert.Contains("NULL", nullValue.Message, StringComparison.Ordinal);

        var missing = await Assert.ThrowsAsync<VelQueryMappingException>(() => ReadStrictText.ExecuteAsync(session, 5, Ct));
        AssertMapping(missing, "strict.text", "Value", typeof(string));
        Assert.Contains("missing", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nullable_members_accept_null_and_missing_and_optional_properties_keep_defaults()
    {
        await using var session = Session();
        Assert.Null((await ReadOptionalText.ExecuteAsync(session, 4, Ct)).Value);
        Assert.Null((await ReadOptionalText.ExecuteAsync(session, 5, Ct)).Value);
        Assert.Equal(SecretText, (await ReadOptionalText.ExecuteAsync(session, 8, Ct)).Value);
        Assert.Equal(42, (await ReadDefaults.ExecuteAsync(session, 5, Ct)).Count);
    }

    [Fact]
    public async Task Duplicate_fields_are_rejected_as_ambiguous()
    {
        var duplicated = new BsonDocument(allowDuplicateNames: true) { { "_id", 100 }, { "Value", 1 }, { "Value", 2 } };
        await Fixture.Raw("strict").InsertOneAsync(duplicated, cancellationToken: Ct);
        await using var session = Session();
        var error = await Assert.ThrowsAsync<VelQueryMappingException>(() => ReadStrictInt.ExecuteAsync(session, 100, Ct));
        AssertMapping(error, "strict.int", "Value", typeof(int));
        Assert.Contains("more than once", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mapping_failure_releases_the_connection()
    {
        using var source = Fixture.CreateDataSource(s =>
        {
            s.MaxConnectionPoolSize = 1;
            s.WaitQueueTimeout = TimeSpan.FromSeconds(2);
        });
        for (var i = 0; i < 20; i++)
        {
            await using var session = source.CreateSession();
            await Assert.ThrowsAsync<VelQueryMappingException>(() => ReadStrictInt.ExecuteAsync(session, 3, Ct));
        }

        await using var last = source.CreateSession();
        Assert.Equal(42, (await ReadStrictInt.ExecuteAsync(last, 1, Ct)).Value);
    }

    private static void AssertMapping(VelQueryMappingException error, string queryId, string column, Type target)
    {
        Assert.Equal(VelQueryErrorCategory.Mapping, error.Category);
        Assert.Equal(queryId, error.QueryId);
        Assert.Equal(column, error.Column);
        Assert.Equal(target, error.TargetType);
        Assert.Contains(queryId, error.Message, StringComparison.Ordinal);
    }
}
