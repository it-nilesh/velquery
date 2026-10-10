using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using VelQuery.Mapping;
using Xunit;

namespace VelQuery.Generators.RuntimeTests;

/// <summary>
/// Executes generated contracts end to end through a real <see cref="VelQuerySession"/> over an in-memory connection
/// whose readers are <see cref="DataTableReader"/>s, and exercises generated mappers directly.
/// </summary>
public sealed class GeneratedContractTests : IDisposable
{
    private static readonly DataTable EmptyUsers = Tables.Create(("Id", typeof(long)), ("Name", typeof(string)), ("Email", typeof(string)));

    private readonly FakeConnection _connection = new();

    public void Dispose() => _connection.Dispose();

    private VelQuerySession Session(bool allowFallback = false)
        => new VelQueryDataSource(new TestProvider(), connectionFactory: null, new VelQueryOptions { AllowDynamicFallback = allowFallback })
            .CreateSession(_connection);

    [Fact]
    public async Task Record_contract_maps_row_and_binds_typed_parameter()
    {
        _connection.Result = EmptyUsers.Clone().WithRows([42L, "Ada", null]);
        await using var session = Session();

        var user = await UsersById.ExecuteAsync(session, 42, TestContext.Current.CancellationToken);

        Assert.Equal(new UserRow(42, "Ada", null), user);
        var command = Assert.Single(_connection.Executed);
        Assert.Equal("SELECT Id, Name, Email FROM dbo.Users WHERE Id = @Id", command.Text);
        Assert.Equal(CommandType.Text, command.Type);
        var parameter = Assert.Single(command.Parameters);
        Assert.Equal(new BoundParameter("@Id", DbType.Int64, 0, 0, 0, 42L), parameter);
    }

    [Fact]
    public async Task Nullable_result_contract_returns_null_for_no_rows_and_throws_for_many()
    {
        await using var session = Session();
        _connection.Result = EmptyUsers.Clone();
        Assert.Null(await UsersById.ExecuteAsync(session, 1, TestContext.Current.CancellationToken));

        _connection.Result = EmptyUsers.Clone().WithRows([1L, "a", null], [2L, "b", null]);
        await Assert.ThrowsAsync<VelQueryTooManyRowsException>(() => UsersById.ExecuteAsync(session, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Null_into_non_nullable_member_fails_strictly_with_column_and_type()
    {
        _connection.Result = EmptyUsers.Clone().WithRows([42L, null, "a@b.c"]);
        await using var session = Session();

        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => UsersById.ExecuteAsync(session, 42, TestContext.Current.CancellationToken));

        Assert.Equal("Name", ex.Column);
        Assert.Equal(typeof(string), ex.TargetType);
        Assert.Equal("users.by-id", ex.QueryId);
        Assert.DoesNotContain("a@b.c", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_required_column_fails_before_reading_rows()
    {
        _connection.Result = Tables.Create(("Id", typeof(long)), ("Email", typeof(string))).WithRows([1L, null]);
        await using var session = Session();

        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => UsersById.ExecuteAsync(session, 1, TestContext.Current.CancellationToken));

        Assert.Equal("Name", ex.Column);
        Assert.Contains("not present", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_matching_columns_are_rejected()
    {
        // Two result columns that match one member case-insensitively (e.g. a JOIN without aliases).
        var duplicate = new DataTable { CaseSensitive = true };
        duplicate.Columns.Add("Id", typeof(long));
        duplicate.Columns.Add("Name", typeof(string));
        duplicate.Columns.Add("Email", typeof(string));
        duplicate.Columns.Add("EMAIL", typeof(string));
        _connection.Result = duplicate.WithRows([1L, "a", null, null]);
        await using var session = Session();

        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => UsersById.ExecuteAsync(session, 1, TestContext.Current.CancellationToken));

        Assert.Equal("Email", ex.Column);
        Assert.Contains("more than one column", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Column_names_match_case_insensitively_and_widen_losslessly()
    {
        _connection.Result = Tables.Create(("ID", typeof(int)), ("name", typeof(string)), ("EMAIL", typeof(string)), ("Extra", typeof(Guid)))
            .WithRows([7, "Grace", "g@h.i", Guid.Empty]);
        await using var session = Session();

        var user = await UsersById.ExecuteAsync(session, 7, TestContext.Current.CancellationToken);

        Assert.Equal(new UserRow(7, "Grace", "g@h.i"), user);
    }

    [Fact]
    public async Task Lossy_conversions_fail_with_mapping_error()
    {
        _connection.Result = Tables.Create(("Id", typeof(string)), ("Name", typeof(string)), ("Email", typeof(string))).WithRows(["7", "x", null]);
        await using var session = Session();

        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => UsersById.ExecuteAsync(session, 7, TestContext.Current.CancellationToken));

        Assert.Equal("Id", ex.Column);
    }

    [Fact]
    public async Task Class_row_honors_column_attribute_optional_required_and_init_members()
    {
        var created = new DateTimeOffset(2026, 10, 10, 8, 30, 0, TimeSpan.FromHours(2));
        _connection.Result = Tables.Create(("order_id", typeof(long)), ("Total", typeof(decimal)), ("Status", typeof(int)), ("CreatedAt", typeof(DateTimeOffset)), ("Region", typeof(string)))
            .WithRows([10L, 12.50m, 2, created, "EU"], [11L, null, 1, created, "US"]);
        await using var session = Session();

        var orders = await OrdersSince.ExecuteAsync(session, created, customer: null, TestContext.Current.CancellationToken);

        Assert.Collection(orders,
            o =>
            {
                Assert.Equal(10, o.Id);
                Assert.Equal("unknown", o.Customer); // optional column absent: initializer value kept
                Assert.Equal(12.50m, o.Total);
                Assert.Equal(OrderStatus.Shipped, o.Status); // int column converted losslessly to a byte enum
                Assert.Equal(created, o.CreatedAt);
                Assert.Equal("EU", o.Region);
            },
            o =>
            {
                Assert.Null(o.Total);
                Assert.Equal(OrderStatus.Open, o.Status);
            });

        var parameters = Assert.Single(_connection.Executed).Parameters;
        Assert.Equal(
            [new BoundParameter("@Since", DbType.DateTimeOffset, 0, 0, 0, created), new BoundParameter("@Customer", DbType.String, 0, 0, 0, DBNull.Value)],
            parameters);
    }

    [Fact]
    public async Task Required_init_member_column_is_required()
    {
        _connection.Result = Tables.Create(("order_id", typeof(long)), ("CreatedAt", typeof(DateTimeOffset))).WithRows([1L, DateTimeOffset.UnixEpoch]);
        await using var session = Session();

        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => OrdersSince.ExecuteAsync(session, DateTimeOffset.UnixEpoch, "x", TestContext.Current.CancellationToken));

        Assert.Equal("Region", ex.Column);
    }

    [Fact]
    public async Task Stream_contract_binds_enum_as_integral_value_and_streams_rows()
    {
        _connection.Result = EmptyUsers.Clone().WithRows([1L, "a", null], [2L, "b", "b@x"]);
        await using var session = Session();

        var rows = new List<UserRow>();
        await foreach (var row in UsersByState.StreamAsync(session, UserState.Disabled, TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        Assert.Equal([new UserRow(1, "a", null), new UserRow(2, "b", "b@x")], rows);
        var parameter = Assert.Single(Assert.Single(_connection.Executed).Parameters);
        Assert.Equal(new BoundParameter("@State", DbType.Int32, 0, 0, 0, 2), parameter);
    }

    [Fact]
    public async Task Scalar_contract_uses_execute_scalar_and_descriptor_timeout()
    {
        _connection.Result = Tables.Create(("", typeof(long))).WithRows([123L]);
        await using var session = Session();

        var count = await CountUsers.ExecuteAsync(session, TestContext.Current.CancellationToken);

        Assert.Equal(123L, count);
        var command = Assert.Single(_connection.Executed);
        Assert.Equal(30, command.Timeout);
        Assert.Empty(command.Parameters);
    }

    [Fact]
    public async Task First_scalar_row_contract_throws_when_no_rows()
    {
        await using var session = Session();
        _connection.Result = Tables.Create(("Name", typeof(string))).WithRows(["first"], ["second"]);
        Assert.Equal("first", await FirstUserName.ExecuteAsync(session, TestContext.Current.CancellationToken));

        _connection.Result = Tables.Create(("Name", typeof(string)));
        await Assert.ThrowsAsync<VelQueryNoRowsException>(() => FirstUserName.ExecuteAsync(session, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NonQuery_contract_binds_overrides_nulls_and_nullable_enums()
    {
        _connection.AffectedRows = 3;
        await using var session = Session();

        var affected = await SetEmail.ExecuteAsync(session, 5, email: null, UserState.Active, TestContext.Current.CancellationToken);
        await SetEmail.ExecuteAsync(session, 6, "x@y.z", state: null, TestContext.Current.CancellationToken);

        Assert.Equal(3, affected);
        Assert.Equal(
            [
                new BoundParameter("@Email", DbType.AnsiString, 320, 0, 0, DBNull.Value),
                new BoundParameter("@State", DbType.Int32, 0, 0, 0, 1),
                new BoundParameter("@Id", DbType.Int64, 0, 0, 0, 5L),
            ],
            _connection.Executed[0].Parameters);
        Assert.Equal(DBNull.Value, _connection.Executed[1].Parameters[1].Value);
        Assert.Equal(DbType.Int32, _connection.Executed[1].Parameters[1].DbType); // type metadata retained for NULL
    }

    [Fact]
    public async Task Stored_procedure_contract_binds_every_parameter_by_name()
    {
        await using var session = Session();

        await ArchiveAudit.ExecuteAsync(session, 1.5m, batchSize: 100);

        var command = Assert.Single(_connection.Executed);
        Assert.Equal(CommandType.StoredProcedure, command.Type);
        Assert.Equal("dbo.ArchiveAudit", command.Text);
        Assert.Equal(
            [new BoundParameter("@threshold", DbType.Decimal, 0, 18, 4, 1.5m), new BoundParameter("@Batch", DbType.Int32, 0, 0, 0, 100)],
            command.Parameters);
    }

    [Fact]
    public async Task Module_initializer_registers_generated_factory_for_runtime_sql()
    {
        Assert.IsType<VelQuery.Generated.RowMapperFactory_VelQuery_Generators_RuntimeTests_UserRow>(RowMappers<UserRow>.Factory);
        Assert.IsType<VelQuery.Generated.RowMapperFactory_VelQuery_Generators_RuntimeTests_OrderRow>(RowMappers<OrderRow>.Factory);

        // Runtime SQL with dynamic fallback disabled resolves the generated, reflection-free mapper.
        _connection.Result = EmptyUsers.Clone().WithRows([9L, "Linus", null]);
        await using var session = Session(allowFallback: false);
        var users = await session.QueryAsync<UserRow>("SELECT Id, Name, Email FROM dbo.Users", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([new UserRow(9, "Linus", null)], users);
    }

    [Fact]
    public void Generated_factory_maps_a_data_table_reader_directly_and_respects_non_strict_mode()
    {
        var table = EmptyUsers.Clone().WithRows([1L, null, null]);
        using var reader = table.CreateDataReader();
        var factory = RowMappers<UserRow>.Factory!;

        var strict = factory.Create(reader, new MappingContext("direct", StrictNulls: true));
        var lenient = factory.Create(reader, new MappingContext("direct", StrictNulls: false));
        Assert.True(reader.Read());

        Assert.Throws<VelQueryMappingException>(() => strict.Map(reader));
        Assert.Equal(new UserRow(1, null!, null), lenient.Map(reader));
    }
}
