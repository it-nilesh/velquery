namespace VelQuery.Generators.GoldenTests;

/// <summary>Representative contracts whose generated source is pinned by checked-in expected files.</summary>
internal static class GoldenCases
{
    public const string RecordRow = """
        using System.Threading;
        using System.Threading.Tasks;
        using VelQuery;

        namespace Sample.Users;

        public sealed record UserRow(long Id, string Name, string? Email);

        [VelQuerySql("users.by-id", "SELECT Id, Name, Email FROM dbo.Users WHERE Id = @Id")]
        public static partial class UsersById
        {
            public static partial Task<UserRow?> ExecuteAsync(IVelQuerySession session, long id, CancellationToken cancellationToken = default);
        }
        """;

    public const string ClassRow = """
        using System;
        using System.Collections.Generic;
        using System.ComponentModel.DataAnnotations.Schema;
        using System.Threading;
        using System.Threading.Tasks;
        using VelQuery;

        namespace Sample.Orders;

        public enum OrderStatus : byte { Open = 1, Shipped = 2 }

        public sealed class OrderRow
        {
            [Column("order_id")]
            public long Id { get; set; }

            public string Customer { get; set; } = "";

            public decimal? Total { get; set; }

            public OrderStatus Status { get; set; }

            public DateTimeOffset CreatedAt { get; init; }

            public required string Region { get; set; }

            [NotMapped]
            public string Display => $"{Id}:{Customer}";
        }

        public static partial class Queries
        {
            [VelQuerySql("orders.since", "SELECT order_id, Customer, Total, Status, CreatedAt, Region FROM dbo.Orders WHERE CreatedAt >= @Since AND (@Customer IS NULL OR Customer = @Customer)")]
            internal static partial class OrdersSince
            {
                internal static partial Task<IReadOnlyList<OrderRow>> ExecuteAsync(IVelQuerySession session, DateOnly since, string? customer, CancellationToken cancellationToken);
            }
        }
        """;

    public const string Scalar = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using VelQuery;

        namespace Sample.Users;

        [VelQuerySql("users.count", "SELECT COUNT_BIG(*) FROM dbo.Users -- WHERE Name = @NotAParameter", Cardinality = QueryCardinality.Scalar, CommandTimeoutSeconds = 30)]
        public static partial class CountUsers
        {
            public static partial Task<long> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
        }

        [VelQuerySql("users.names", "SELECT Name FROM dbo.Users WHERE Name LIKE N'%@x%' ORDER BY [Name@]")]
        public static partial class UserNames
        {
            public static partial Task<IReadOnlyList<string>> ExecuteAsync(VelQuerySession session);
        }
        """;

    public const string Stream = """
        using System.Collections.Generic;
        using System.Threading;
        using VelQuery;

        namespace Sample.Users;

        public enum UserState { Active = 1, Disabled = 2 }

        public sealed record UserRow(long Id, string Name, string? Email, UserState State);

        [VelQuerySql("users.by-state", "SELECT Id, Name, Email, State FROM dbo.Users WHERE State = @state /* and @@ROWCOUNT > 0 */")]
        public static partial class UsersByState
        {
            public static partial IAsyncEnumerable<UserRow> StreamAsync(IVelQuerySession session, UserState state, CancellationToken cancellationToken = default);
        }
        """;

    public const string NonQuery = """
        using System.Data;
        using System.Threading;
        using System.Threading.Tasks;
        using VelQuery;

        namespace Sample.Users;

        [VelQuerySql("users.set-email", "UPDATE dbo.Users SET Email = @Email, State = @State WHERE Id = @Id", Cardinality = QueryCardinality.NonQuery)]
        public static partial class SetEmail
        {
            public static partial Task<int> ExecuteAsync(
                IVelQuerySession session,
                long id,
                [VelQueryParameter(DbType = DbType.AnsiString, Size = 320)] string? email,
                Sample.Users.UserState? state,
                CancellationToken cancellationToken = default);
        }

        public enum UserState : short { Active = 1, Disabled = 2 }
        """;

    public const string StoredProcedure = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using VelQuery;

        namespace Sample.Audit;

        [VelQuerySql("audit.archive", "dbo.ArchiveAudit", CommandType = System.Data.CommandType.StoredProcedure, Cardinality = QueryCardinality.NonQuery)]
        public static partial class ArchiveAudit
        {
            public static partial Task<int> ExecuteAsync(
                IVelQuerySession session,
                DateTime before,
                [VelQueryParameter(Precision = 18, Scale = 4)] decimal threshold,
                [VelQueryParameter(Name = "Batch")] int? batchSize,
                Guid? tenant);
        }
        """;
}
