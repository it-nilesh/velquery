using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace VelQuery.MongoDB.IntegrationTests;

// ---------------------------------------------------------------------------------------------------------------
// Document types
// ---------------------------------------------------------------------------------------------------------------

public enum Level
{
    None = 0,
    Low = 1,
    High = 2,
}

public sealed record Person(int Id, string Name, int Age, string Status);

public sealed record PersonWithSecret(int Id, string Name, string? Secret);

public sealed record BigRow(int Id, string Name, string Payload);

public sealed record StatusCount([property: BsonId] string Status, int Count);

public sealed record Labeled(int Id, string Name, string Label);

public sealed record Address(string City, [property: BsonElement("zip")] string? Zip);

/// <summary>Every supported member shape, for round trips.</summary>
public sealed class TypeDoc
{
    public ObjectId Id { get; set; }

    public required string Key { get; init; }

    public Guid Uid { get; set; }

    public DateTime When { get; set; }

    public decimal Amount { get; set; }

    public Decimal128 Wide { get; set; }

    public int Number { get; set; }

    public long BigNumber { get; set; }

    public double Ratio { get; set; }

    public bool Flag { get; set; }

    public string Text { get; set; } = string.Empty;

    public byte[] Bytes { get; set; } = [];

    public Level Level { get; set; }

    public Level? MaybeLevel { get; set; }

    public int? MaybeInt { get; set; }

    public string? MaybeText { get; set; }

    public DateTime? MaybeWhen { get; set; }

    public List<string> Tags { get; set; } = [];

    public int[] Scores { get; set; } = [];

    public IReadOnlyList<long?> Sparse { get; set; } = [];

    public Address Home { get; set; } = new("nowhere", null);

    public List<Address> Others { get; set; } = [];

    public Address? Maybe { get; set; }

    public List<List<int>> Matrix { get; set; } = [];

    public BsonDocument Extra { get; set; } = [];

    [BsonIgnoreIfNull]
    public string? Omitted { get; set; }

    [BsonIgnore]
    public string Ignored { get; set; } = "not stored";

    public string Computed => "computed " + Text;
}

public sealed record StrictInt(int Id, int Value);

public sealed record StrictLong(int Id, long Value);

public sealed record StrictDouble(int Id, double Value);

public sealed record StrictDecimal(int Id, decimal Value);

public sealed record StrictText(int Id, string Value);

public sealed record StrictGuid(int Id, Guid Value);

public sealed record OptionalText(int Id, string? Value);

public sealed class Defaults
{
    public int Id { get; set; }

    public int Count { get; set; } = 42;
}

public sealed record Account(int Id, string Owner, decimal Balance);

public sealed record Item([property: BsonId] ObjectId Key, string Sku, int Qty);

// ---------------------------------------------------------------------------------------------------------------
// Find contracts
// ---------------------------------------------------------------------------------------------------------------

[VelQueryMongoFind("people.byStatus", "people", """{ "Status": @status, "Age": { "$gte": @minAge } }""", Sort = """{ "_id": 1 }""")]
public static partial class PeopleByStatus
{
    public static partial Task<IReadOnlyList<Person>> ExecuteAsync(VelQueryMongoSession session, string status, int minAge, CancellationToken cancellationToken);
}

[VelQueryMongoFind("people.byId", "people", """{ "_id": @id }""")]
public static partial class PersonById
{
    public static partial Task<Person?> ExecuteAsync(VelQueryMongoSession session, int id, CancellationToken cancellationToken);
}

[VelQueryMongoFind("people.byId.required", "people", """{ "_id": @id }""")]
public static partial class PersonByIdRequired
{
    public static partial Task<Person> ExecuteAsync(VelQueryMongoSession session, int id, CancellationToken cancellationToken);
}

[VelQueryMongoFind("people.first", "people", """{ "Status": @status }""", Sort = """{ "_id": -1 }""", Cardinality = QueryCardinality.First)]
public static partial class FirstPerson
{
    public static partial Task<Person> ExecuteAsync(VelQueryMongoSession session, string status, CancellationToken cancellationToken);
}

[VelQueryMongoFind("people.firstOrDefault", "people", """{ "Status": @status }""", Sort = """{ "_id": 1 }""", Cardinality = QueryCardinality.FirstOrDefault)]
public static partial class FirstPersonOrDefault
{
    public static partial Task<Person?> ExecuteAsync(VelQueryMongoSession session, string status, CancellationToken cancellationToken);
}

[VelQueryMongoFind("people.single", "people", """{ "Status": @status }""", Cardinality = QueryCardinality.Single)]
public static partial class SinglePerson
{
    public static partial Task<Person> ExecuteAsync(VelQueryMongoSession session, string status, CancellationToken cancellationToken);
}

[VelQueryMongoFind("people.page", "people", """{ }""", Sort = """{ "_id": 1 }""", Skip = 10, Limit = 5)]
public static partial class PeoplePage
{
    public static partial Task<IReadOnlyList<Person>> ExecuteAsync(VelQueryMongoSession session, CancellationToken cancellationToken);
}

[VelQueryMongoFind("people.byName", "people", """{ "Name": @name }""")]
public static partial class PeopleByName
{
    public static partial Task<IReadOnlyList<Person>> ExecuteAsync(VelQueryMongoSession session, string? name, CancellationToken cancellationToken);
}

[VelQueryMongoFind("people.byNames", "people", """{ "Name": { "$in": @names } }""", Sort = """{ "_id": 1 }""")]
public static partial class PeopleByNames
{
    public static partial Task<IReadOnlyList<Person>> ExecuteAsync(VelQueryMongoSession session, IEnumerable<string> names, CancellationToken cancellationToken);
}

[VelQueryMongoFind("people.byNameExpr", "people", """{ "$expr": { "$eq": [ "$Name", @name ] } }""")]
public static partial class PeopleByNameExpression
{
    public static partial Task<IReadOnlyList<Person>> ExecuteAsync(VelQueryMongoSession session, string name, CancellationToken cancellationToken);
}

[VelQueryMongoFind("people.byNameRegex", "people", """{ "Name": { "$regex": @pattern } }""")]
public static partial class PeopleByNameRegex
{
    public static partial Task<IReadOnlyList<Person>> ExecuteAsync(VelQueryMongoSession session, string pattern, CancellationToken cancellationToken);
}

[VelQueryMongoFind("people.withoutSecret", "people", """{ "_id": @id }""", Projection = """{ "Secret": 0 }""")]
public static partial class PersonWithoutSecret
{
    public static partial Task<PersonWithSecret> ExecuteAsync(VelQueryMongoSession session, int id, CancellationToken cancellationToken);
}

[VelQueryMongoFind("big.all", "big", """{ }""", Sort = """{ "_id": 1 }""")]
public static partial class StreamBig
{
    public static partial IAsyncEnumerable<BigRow> ExecuteAsync(VelQueryMongoSession session, CancellationToken cancellationToken);
}

[VelQueryMongoFind("big.all.buffered", "big", """{ }""")]
public static partial class AllBig
{
    public static partial Task<IReadOnlyList<BigRow>> ExecuteAsync(VelQueryMongoSession session, CancellationToken cancellationToken);
}

/// <summary>
/// A deliberately slow scan: every document builds a 20,000-element $range ($where- and JavaScript-free). The bound
/// depends on a field so the server cannot constant-fold the expression.
/// </summary>
[VelQueryMongoFind("big.slow", "big", """{ "$expr": { "$gt": [ { "$size": { "$range": [ 0, { "$add": [ 20000, { "$multiply": [ "$_id", 0 ] } ] } ] } }, @threshold ] } }""", TimeoutMilliseconds = 300)]
public static partial class SlowFind
{
    public static partial Task<IReadOnlyList<BigRow>> ExecuteAsync(VelQueryMongoSession session, int threshold, CancellationToken cancellationToken);
}

[VelQueryMongoFind("big.slow.untimed", "big", """{ "$expr": { "$gt": [ { "$size": { "$range": [ 0, { "$add": [ 20000, { "$multiply": [ "$_id", 0 ] } ] } ] } }, @threshold ] } }""")]
public static partial class SlowFindUntimed
{
    public static partial Task<IReadOnlyList<BigRow>> ExecuteAsync(VelQueryMongoSession session, int threshold, CancellationToken cancellationToken);
}

[VelQueryMongoFind("big.slow.stream", "big", """{ "$expr": { "$gt": [ { "$size": { "$range": [ 0, { "$add": [ 20000, { "$multiply": [ "$_id", 0 ] } ] } ] } }, @threshold ] } }""", TimeoutMilliseconds = 300)]
public static partial class SlowFindStreamed
{
    public static partial IAsyncEnumerable<BigRow> ExecuteAsync(VelQueryMongoSession session, int threshold, CancellationToken cancellationToken);
}

[VelQueryMongoFind("types.byKey", "types", """{ "Key": @key }""")]
public static partial class TypeDocByKey
{
    public static partial Task<TypeDoc> ExecuteAsync(VelQueryMongoSession session, string key, CancellationToken cancellationToken);
}

[VelQueryMongoFind("types.match", "types", """{ "Key": @key, "Uid": @uid, "When": @when, "Amount": @amount, "Level": @level, "Flag": @flag, "BigNumber": @l }""")]
public static partial class TypeDocMatch
{
    public static partial Task<TypeDoc?> ExecuteAsync(VelQueryMongoSession session, string key, Guid uid, DateTime when, decimal amount, Level level, bool flag, long l, CancellationToken cancellationToken);
}

[VelQueryMongoFind("strict.int", "strict", """{ "_id": @id }""")]
public static partial class ReadStrictInt
{
    public static partial Task<StrictInt> ExecuteAsync(VelQueryMongoSession session, int id, CancellationToken cancellationToken);
}

[VelQueryMongoFind("strict.long", "strict", """{ "_id": @id }""")]
public static partial class ReadStrictLong
{
    public static partial Task<StrictLong> ExecuteAsync(VelQueryMongoSession session, int id, CancellationToken cancellationToken);
}

[VelQueryMongoFind("strict.double", "strict", """{ "_id": @id }""")]
public static partial class ReadStrictDouble
{
    public static partial Task<StrictDouble> ExecuteAsync(VelQueryMongoSession session, int id, CancellationToken cancellationToken);
}

[VelQueryMongoFind("strict.decimal", "strict", """{ "_id": @id }""")]
public static partial class ReadStrictDecimal
{
    public static partial Task<StrictDecimal> ExecuteAsync(VelQueryMongoSession session, int id, CancellationToken cancellationToken);
}

[VelQueryMongoFind("strict.text", "strict", """{ "_id": @id }""")]
public static partial class ReadStrictText
{
    public static partial Task<StrictText> ExecuteAsync(VelQueryMongoSession session, int id, CancellationToken cancellationToken);
}

[VelQueryMongoFind("strict.guid", "strict", """{ "_id": @id }""")]
public static partial class ReadStrictGuid
{
    public static partial Task<StrictGuid> ExecuteAsync(VelQueryMongoSession session, int id, CancellationToken cancellationToken);
}

[VelQueryMongoFind("strict.optional", "strict", """{ "_id": @id }""")]
public static partial class ReadOptionalText
{
    public static partial Task<OptionalText> ExecuteAsync(VelQueryMongoSession session, int id, CancellationToken cancellationToken);
}

[VelQueryMongoFind("strict.defaults", "strict", """{ "_id": @id }""")]
public static partial class ReadDefaults
{
    public static partial Task<Defaults> ExecuteAsync(VelQueryMongoSession session, int id, CancellationToken cancellationToken);
}

[VelQueryMongoFind("accounts.all", "accounts", """{ }""", Sort = """{ "_id": 1 }""")]
public static partial class AllAccounts
{
    public static partial Task<IReadOnlyList<Account>> ExecuteAsync(VelQueryMongoSession session, CancellationToken cancellationToken);
}

[VelQueryMongoFind("accounts.stream", "accounts", """{ }""", Sort = """{ "_id": 1 }""")]
public static partial class StreamAccounts
{
    public static partial IAsyncEnumerable<Account> ExecuteAsync(VelQueryMongoSession session, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------------------------------------------
// Count and aggregate contracts
// ---------------------------------------------------------------------------------------------------------------

[VelQueryMongoCount("people.countByStatus", "people", """{ "Status": @status }""")]
public static partial class CountByStatus
{
    public static partial Task<long> ExecuteAsync(VelQueryMongoSession session, string status, CancellationToken cancellationToken);
}

[VelQueryMongoCount("accounts.count", "accounts", """{ }""")]
public static partial class CountAccounts
{
    public static partial Task<long> ExecuteAsync(VelQueryMongoSession session, CancellationToken cancellationToken);
}

[VelQueryMongoAggregate("people.countsByStatus", "people", """
    [
      { "$group": { "_id": "$Status", "Count": { "$sum": 1 } } },
      { "$sort": { "_id": 1 } }
    ]
    """)]
public static partial class CountsByStatus
{
    public static partial Task<IReadOnlyList<StatusCount>> ExecuteAsync(VelQueryMongoSession session, CancellationToken cancellationToken);
}

[VelQueryMongoAggregate("people.labeled", "people", """
    [
      { "$match": { "_id": @id } },
      { "$project": { "Name": 1, "Label": @label } }
    ]
    """)]
public static partial class LabeledPerson
{
    public static partial Task<Labeled?> ExecuteAsync(VelQueryMongoSession session, int id, string label, CancellationToken cancellationToken);
}

[VelQueryMongoAggregate("people.oldest", "people", """
    [
      { "$match": { "Status": @status } },
      { "$sort": { "Age": -1, "_id": 1 } },
      { "$limit": @take }
    ]
    """)]
public static partial class OldestPeople
{
    public static partial IAsyncEnumerable<Person> ExecuteAsync(VelQueryMongoSession session, string status, int take, CancellationToken cancellationToken);
}

[VelQueryMongoAggregate("people.aggregateSingle", "people", """[ { "$match": { "Status": @status } } ]""", Cardinality = QueryCardinality.Single)]
public static partial class AggregateSingle
{
    public static partial Task<Person> ExecuteAsync(VelQueryMongoSession session, string status, CancellationToken cancellationToken);
}

[VelQueryMongoAggregate("big.slowAggregate", "big", """
    [
      { "$project": { "n": { "$size": { "$range": [ 0, { "$add": [ 20000, { "$multiply": [ "$_id", 0 ] } ] } ] } } } },
      { "$group": { "_id": null, "Count": { "$sum": "$n" } } },
      { "$project": { "_id": "x", "Count": 1 } }
    ]
    """, TimeoutMilliseconds = 300)]
public static partial class SlowAggregate
{
    public static partial Task<IReadOnlyList<StatusCount>> ExecuteAsync(VelQueryMongoSession session, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------------------------------------------
// Write contracts
// ---------------------------------------------------------------------------------------------------------------

[VelQueryMongoInsert("accounts.insert", "accounts")]
public static partial class InsertAccount
{
    public static partial Task ExecuteAsync(VelQueryMongoSession session, Account account, CancellationToken cancellationToken);
}

[VelQueryMongoInsert("accounts.insertMany", "accounts")]
public static partial class InsertAccounts
{
    public static partial Task<long> ExecuteAsync(VelQueryMongoSession session, IEnumerable<Account> accounts, CancellationToken cancellationToken);
}

[VelQueryMongoInsert("types.insert", "types")]
public static partial class InsertTypeDoc
{
    public static partial Task ExecuteAsync(VelQueryMongoSession session, TypeDoc document, CancellationToken cancellationToken);
}

[VelQueryMongoInsert("items.insertMany", "items")]
public static partial class InsertItems
{
    public static partial Task<long> ExecuteAsync(VelQueryMongoSession session, IEnumerable<Item> items, CancellationToken cancellationToken);
}

[VelQueryMongoFind("items.all", "items", """{ }""")]
public static partial class AllItems
{
    public static partial Task<IReadOnlyList<Item>> ExecuteAsync(VelQueryMongoSession session, CancellationToken cancellationToken);
}

[VelQueryMongoUpdate("accounts.deposit", "accounts", """{ "_id": @id }""", """{ "$inc": { "Balance": @amount } }""")]
public static partial class Deposit
{
    public static partial Task<VelQueryMongoUpdateResult> ExecuteAsync(VelQueryMongoSession session, int id, decimal amount, CancellationToken cancellationToken);
}

[VelQueryMongoUpdate("accounts.rename", "accounts", """{ "Owner": @from }""", """{ "$set": { "Owner": @to } }""", Many = true)]
public static partial class RenameOwner
{
    public static partial Task<VelQueryMongoUpdateResult> ExecuteAsync(VelQueryMongoSession session, string from, string to, CancellationToken cancellationToken);
}

[VelQueryMongoUpdate("accounts.upsert", "accounts", """{ "_id": @id }""", """{ "$set": { "Owner": @owner, "Balance": @balance } }""", Upsert = true)]
public static partial class UpsertAccount
{
    public static partial Task<VelQueryMongoUpdateResult> ExecuteAsync(VelQueryMongoSession session, int id, string owner, decimal balance, CancellationToken cancellationToken);
}

[VelQueryMongoDelete("accounts.delete", "accounts", """{ "_id": @id }""")]
public static partial class DeleteAccount
{
    public static partial Task<long> ExecuteAsync(VelQueryMongoSession session, int id, CancellationToken cancellationToken);
}

[VelQueryMongoDelete("accounts.deleteByOwner", "accounts", """{ "Owner": @owner }""", Many = true)]
public static partial class DeleteByOwner
{
    public static partial Task<long> ExecuteAsync(VelQueryMongoSession session, string owner, CancellationToken cancellationToken);
}
