namespace VelQuery.MongoDB.Generators.Tests;

/// <summary>Contract sources whose generated output is pinned byte-for-byte under <c>Expected/</c>.</summary>
internal static class GoldenCases
{
    public const string Find = """"
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using MongoDB.Bson;
        using MongoDB.Bson.Serialization.Attributes;
        using VelQuery.MongoDB;

        namespace Sample.Users;

        public enum Role { Reader = 1, Writer = 2 }

        public sealed record Address(string City, [property: BsonElement("zip")] string? Zip);

        public sealed class User
        {
            public ObjectId Id { get; set; }

            public required string Name { get; init; }

            public int Age { get; set; }

            public Guid ExternalId { get; set; }

            public Role Role { get; set; }

            public Role? PreviousRole { get; set; }

            public DateTime CreatedAt { get; set; }

            public decimal Balance { get; set; }

            public List<string> Tags { get; set; } = [];

            public Address? Home { get; set; }

            public Address[] Others { get; set; } = [];

            [BsonIgnoreIfNull]
            public string? Nickname { get; set; }

            [BsonIgnore]
            public string Display => Name;
        }

        [VelQueryMongoFind("users.active", "users", """{ "Status": @status, "Age": { "$gte": @minAge, "$lt": 130 }, "Role": { "$in": @roles } }""",
            Sort = """{ "Age": -1, "_id": 1 }""", Projection = """{ "Nickname": 0 }""", Limit = 50, Skip = 10, TimeoutMilliseconds = 2000)]
        public static partial class ActiveUsers
        {
            public static partial Task<IReadOnlyList<User>> ExecuteAsync(VelQueryMongoSession session, string status, int minAge, IReadOnlyList<Role> roles, CancellationToken cancellationToken);
        }
        """";

    public const string Aggregate = """"
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using MongoDB.Bson;
        using MongoDB.Bson.Serialization.Attributes;
        using VelQuery.MongoDB;

        namespace Sample.Reports;

        public sealed record Summary([property: BsonId] string Status, long Count, string Label);

        [VelQueryMongoAggregate("orders.summary", "orders", """
            [
              { "$match": { "CustomerId": @customerId, "CreatedAt": { "$gte": { "$date": "2026-01-01T00:00:00Z" } },
                            "Marker": { "$oid": "65f0c0ffee0000000000abcd" }, "Total": { "$gt": { "$numberDecimal": "10.50" } },
                            "$expr": { "$gt": [ "$Total", @minTotal ] } } },
              { "$group": { "_id": "$Status", "Count": { "$sum": 1 } } },
              { "$project": { "Count": 1, "Label": @label, "Raw": { "$literal": @label } } },
              { "$sort": { "Count": -1 } },
              { "$limit": @take }
            ]
            """, AllowDiskUse = true)]
        public static partial class OrderSummary
        {
            public static partial IAsyncEnumerable<Summary> ExecuteAsync(VelQueryMongoSession session, Guid customerId, decimal minTotal, string label, int take, CancellationToken cancellationToken);
        }
        """";

    public const string Writes = """"
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using VelQuery.MongoDB;

        namespace Sample.Accounts;

        public sealed record Account(long Id, string Owner, decimal Balance, DateTime? ClosedAt);

        [VelQueryMongoUpdate("accounts.deposit", "accounts", """{ "_id": @id }""", """{ "$inc": { "Balance": @amount }, "$set": { "ClosedAt": null } }""", Upsert = true)]
        public static partial class Deposit
        {
            public static partial Task<VelQueryMongoUpdateResult> ExecuteAsync(VelQueryMongoSession session, long id, decimal amount, CancellationToken cancellationToken);
        }

        [VelQueryMongoDelete("accounts.closeBefore", "accounts", """{ "ClosedAt": { "$lt": @before } }""", Many = true)]
        public static partial class DeleteClosed
        {
            public static partial Task<long> ExecuteAsync(VelQueryMongoSession session, DateTime before, CancellationToken cancellationToken);
        }

        [VelQueryMongoCount("accounts.open", "accounts", """{ "ClosedAt": null }""")]
        internal static partial class CountOpen
        {
            internal static partial Task<long> ExecuteAsync(VelQueryMongoSession session);
        }

        [VelQueryMongoInsert("accounts.bulk", "accounts")]
        public static partial class InsertAccounts
        {
            public static partial Task<long> ExecuteAsync(VelQueryMongoSession session, IEnumerable<Account> accounts, CancellationToken cancellationToken);
        }

        [VelQueryMongoInsert("accounts.one", "accounts")]
        public static partial class InsertAccount
        {
            public static partial Task ExecuteAsync(VelQueryMongoSession session, Account account, CancellationToken cancellationToken);
        }
        """";

    public const string Single = """"
        using System.Threading.Tasks;
        using VelQuery;
        using VelQuery.MongoDB;

        namespace Sample.Lookup;

        public sealed record Product(string Id, string Name, int[][] Grid);

        public static partial class Catalog
        {
            [VelQueryMongoFind("products.bySku", "products", """{ "_id": @sku }""")]
            public static partial class BySku
            {
                public static partial Task<Product?> ExecuteAsync(VelQueryMongoSession session, string sku);
            }

            [VelQueryMongoFind("products.first", "products", """{ "Name": { "$regularExpression": { "pattern": "^a", "options": "i" } } }""", Cardinality = QueryCardinality.First)]
            public static partial class FirstA
            {
                public static partial Task<Product> ExecuteAsync(VelQueryMongoSession session);
            }
        }
        """";
}
