using System;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace VelQuery.MongoDB.IntegrationTests;

/// <summary>Type fidelity of the generated serializer through a real server (write, read back, inspect raw BSON).</summary>
public sealed class TypeRoundTripTests(MongoFixture fixture) : IntegrationTest(fixture)
{
    private static TypeDoc Sample(string key) => new()
    {
        Key = key,
        Uid = Guid.Parse("a3c1d865-9684-46a4-b000-000000000001"),
        When = new DateTime(2026, 10, 20, 8, 30, 15, 123, DateTimeKind.Utc),
        Amount = 79228162514264337593543950335m,
        Wide = Decimal128.Parse("1.234567890123456789012345678901234E+6000"),
        Number = int.MinValue,
        BigNumber = long.MaxValue,
        Ratio = 0.1 + 0.2,
        Flag = true,
        Text = "💡 unicode ✓ é 中文 " + new string('ü', 2000),
        Bytes = [0x00, 0xff, 0x10],
        Level = Level.High,
        MaybeLevel = null,
        MaybeInt = 7,
        MaybeText = null,
        MaybeWhen = DateTime.UnixEpoch,
        Tags = ["a", "b", "ünï"],
        Scores = [1, -2, 3],
        Sparse = [1L, null, long.MinValue],
        Home = new Address("Pune", "411001"),
        Others = [new Address("Berlin", null), new Address("Paris", "75001")],
        Maybe = null,
        Matrix = [[1, 2], [], [3]],
        Extra = new BsonDocument { { "free", "form" }, { "n", 1 } },
        Omitted = null,
        Ignored = "should not be stored",
    };

    [Fact]
    public async Task Every_supported_member_type_round_trips()
    {
        await Fixture.Raw("types").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);
        var original = Sample("all-types");
        await using var session = Session();
        await InsertTypeDoc.ExecuteAsync(session, original, Ct);
        var read = await TypeDocByKey.ExecuteAsync(session, "all-types", Ct);

        Assert.NotEqual(ObjectId.Empty, read.Id); // Empty ObjectId _id is omitted, so the server assigned one.
        Assert.Equal(ObjectId.Empty, original.Id); // ... and the caller's document was not mutated.
        Assert.Equal(original.Uid, read.Uid);
        Assert.Equal(original.When, read.When);
        Assert.Equal(DateTimeKind.Utc, read.When.Kind);
        Assert.Equal(original.Amount, read.Amount);
        Assert.Equal(original.Wide, read.Wide);
        Assert.Equal(original.Number, read.Number);
        Assert.Equal(original.BigNumber, read.BigNumber);
        Assert.Equal(original.Ratio, read.Ratio);
        Assert.True(read.Flag);
        Assert.Equal(original.Text, read.Text);
        Assert.Equal(original.Bytes, read.Bytes);
        Assert.Equal(Level.High, read.Level);
        Assert.Null(read.MaybeLevel);
        Assert.Equal(7, read.MaybeInt);
        Assert.Null(read.MaybeText);
        Assert.Equal(DateTime.UnixEpoch, read.MaybeWhen);
        Assert.Equal(original.Tags, read.Tags);
        Assert.Equal(original.Scores, read.Scores);
        Assert.Equal(original.Sparse, read.Sparse);
        Assert.Equal(original.Home, read.Home);
        Assert.Equal(original.Others, read.Others);
        Assert.Null(read.Maybe);
        Assert.Equal(3, read.Matrix.Count);
        Assert.Equal([1, 2], read.Matrix[0]);
        Assert.Empty(read.Matrix[1]);
        Assert.Equal(original.Extra, read.Extra);
        Assert.Equal("not stored", read.Ignored);
    }

    [Fact]
    public async Task Stored_bson_uses_the_documented_representations()
    {
        await Fixture.Raw("types").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);
        await using var session = Session();
        await InsertTypeDoc.ExecuteAsync(session, Sample("raw"), Ct);

        var raw = await Fixture.Raw("types").Find(new BsonDocument("Key", "raw")).SingleAsync(Ct);
        Assert.Equal(BsonType.ObjectId, raw["_id"].BsonType);
        Assert.Equal(BsonBinarySubType.UuidStandard, raw["Uid"].AsBsonBinaryData.SubType);
        Assert.Equal(BsonType.DateTime, raw["When"].BsonType);
        Assert.Equal(BsonType.Decimal128, raw["Amount"].BsonType);
        Assert.Equal(BsonType.Int32, raw["Level"].BsonType);
        Assert.Equal(2, raw["Level"].AsInt32);
        Assert.Equal(BsonType.Null, raw["MaybeLevel"].BsonType);
        Assert.Equal("411001", raw["Home"]["zip"].AsString); // [BsonElement("zip")]
        Assert.False(raw.Contains("Omitted")); // [BsonIgnoreIfNull]
        Assert.False(raw.Contains("Ignored")); // [BsonIgnore]
        Assert.False(raw.Contains("Computed")); // get-only members are not stored
        Assert.Equal("_id", raw.GetElement(0).Name);
    }

    [Fact]
    public async Task Typed_placeholders_bind_with_the_same_representation_the_serializer_writes()
    {
        await Fixture.Raw("types").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);
        var original = Sample("match");
        await using var session = Session();
        await InsertTypeDoc.ExecuteAsync(session, original, Ct);

        var found = await TypeDocMatch.ExecuteAsync(session, "match", original.Uid, original.When, original.Amount, Level.High, true, long.MaxValue, Ct);
        Assert.NotNull(found);
        Assert.Null(await TypeDocMatch.ExecuteAsync(session, "match", Guid.NewGuid(), original.When, original.Amount, Level.High, true, long.MaxValue, Ct));
    }

    [Fact]
    public async Task Local_times_are_stored_as_utc_and_unspecified_kind_is_rejected()
    {
        await Fixture.Raw("types").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);
        await using var session = Session();
        var local = Sample("local");
        local.When = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);
        await InsertTypeDoc.ExecuteAsync(session, local, Ct);
        var read = await TypeDocByKey.ExecuteAsync(session, "local", Ct);
        Assert.Equal(local.When.ToUniversalTime(), read.When);

        var unspecified = Sample("unspecified");
        unspecified.When = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
        var error = await Assert.ThrowsAsync<VelQueryMappingException>(() => InsertTypeDoc.ExecuteAsync(session, unspecified, Ct));
        Assert.Equal("When", error.Column);
        Assert.Equal("types.insert", error.QueryId);
        Assert.Equal(0, await Fixture.Raw("types").CountDocumentsAsync(new BsonDocument("Key", "unspecified"), cancellationToken: Ct));
    }

    [Fact]
    public async Task Sub_millisecond_ticks_are_truncated_as_documented()
    {
        await Fixture.Raw("types").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);
        await using var session = Session();
        var precise = Sample("ticks");
        precise.When = new DateTime(2026, 10, 20, 8, 30, 15, DateTimeKind.Utc).AddTicks(1_234_567);
        await InsertTypeDoc.ExecuteAsync(session, precise, Ct);
        var read = await TypeDocByKey.ExecuteAsync(session, "ticks", Ct);
        Assert.Equal(precise.When.AddTicks(-(precise.When.Ticks % TimeSpan.TicksPerMillisecond)), read.When);
    }

    [Fact]
    public async Task Null_in_a_non_nullable_member_is_rejected_before_anything_is_sent()
    {
        await Fixture.Raw("types").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);
        await using var session = Session();
        var broken = Sample("broken");
        broken.Text = null!;
        var error = await Assert.ThrowsAsync<VelQueryMappingException>(() => InsertTypeDoc.ExecuteAsync(session, broken, Ct));
        Assert.Equal("Text", error.Column);

        var nested = Sample("nested");
        nested.Others = [new Address(null!, null)];
        error = await Assert.ThrowsAsync<VelQueryMappingException>(() => InsertTypeDoc.ExecuteAsync(session, nested, Ct));
        Assert.Equal("Others[].City", error.Column);
        Assert.Equal(0, await Fixture.Raw("types").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct));
    }

    [Fact]
    public async Task Unknown_fields_are_skipped()
    {
        await using var session = Session();
        // The seeded people documents carry a Secret field that Person does not map.
        var person = await PersonByIdRequired.ExecuteAsync(session, 1, Ct);
        Assert.Equal("person-1", person.Name);
        var all = await PeopleByStatus.ExecuteAsync(session, "inactive", 0, Ct);
        Assert.Equal(33, all.Count);
        Assert.All(all, p => Assert.StartsWith("person-", p.Name, StringComparison.Ordinal));
        Assert.True(all.Select(p => p.Id).SequenceEqual(all.Select(p => p.Id).Order()));
    }
}
