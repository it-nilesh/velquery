using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace VelQuery.Generators.RuntimeTests;

// Real [VelQuerySql] contracts compiled by VelQuery.Generators as part of this project.

public sealed record UserRow(long Id, string Name, string? Email);

public enum UserState
{
    Active = 1,
    Disabled = 2,
}

public enum OrderStatus : byte
{
    Open = 1,
    Shipped = 2,
}

public sealed class OrderRow
{
    [Column("order_id")]
    public long Id { get; set; }

    public string Customer { get; set; } = "unknown";

    public decimal? Total { get; set; }

    public OrderStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public required string Region { get; set; }

    [NotMapped]
    public string Display => $"{Id}:{Customer}";
}

[VelQuerySql("users.by-id", "SELECT Id, Name, Email FROM dbo.Users WHERE Id = @Id")]
public static partial class UsersById
{
    public static partial Task<UserRow?> ExecuteAsync(IVelQuerySession session, long id, CancellationToken cancellationToken = default);
}

[VelQuerySql("users.by-state", "SELECT Id, Name, Email FROM dbo.Users WHERE State = @State /* @Ignored */ AND Name <> '@literal'")]
public static partial class UsersByState
{
    public static partial IAsyncEnumerable<UserRow> StreamAsync(IVelQuerySession session, UserState state, CancellationToken cancellationToken = default);
}

[VelQuerySql("orders.since", "SELECT order_id, Customer, Total, Status, CreatedAt, Region FROM dbo.Orders WHERE CreatedAt >= @Since AND (@Customer IS NULL OR Customer = @Customer)")]
public static partial class OrdersSince
{
    public static partial Task<IReadOnlyList<OrderRow>> ExecuteAsync(IVelQuerySession session, DateTimeOffset since, string? customer, CancellationToken cancellationToken = default);
}

[VelQuerySql("users.count", "SELECT COUNT_BIG(*) FROM dbo.Users", Cardinality = QueryCardinality.Scalar, CommandTimeoutSeconds = 30)]
public static partial class CountUsers
{
    public static partial Task<long> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("users.first-name", "SELECT Name FROM dbo.Users ORDER BY Id", Cardinality = QueryCardinality.First)]
public static partial class FirstUserName
{
    public static partial Task<string> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("users.set-email", "UPDATE dbo.Users SET Email = @Email, State = @State WHERE Id = @Id", Cardinality = QueryCardinality.NonQuery)]
public static partial class SetEmail
{
    public static partial Task<int> ExecuteAsync(
        IVelQuerySession session,
        long id,
        [VelQueryParameter(DbType = DbType.AnsiString, Size = 320)] string? email,
        UserState? state,
        CancellationToken cancellationToken = default);
}

[VelQuerySql("audit.archive", "dbo.ArchiveAudit", CommandType = CommandType.StoredProcedure, Cardinality = QueryCardinality.NonQuery)]
public static partial class ArchiveAudit
{
    public static partial Task<int> ExecuteAsync(IVelQuerySession session, [VelQueryParameter(Precision = 18, Scale = 4)] decimal threshold, [VelQueryParameter(Name = "Batch")] int? batchSize);
}
