using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace VelQuery.Sqlite.IntegrationTests;

/// <summary>Cardinality, scalar/execute semantics, parameters, injection, multi-result and in-memory databases on SQLite.</summary>
public sealed class QueryTests(SqliteFixture fixture) : IntegrationTest(fixture)
{
    [Theory]
    [InlineData("rows100", 100)]
    [InlineData("rows100k", SqliteFixture.LargeRowCount)]
    public async Task QueryAsync_returns_every_row_in_order(string table, int expected)
    {
        await using var session = Session();
        var rows = await session.QueryAsync<IdName>($"SELECT id, name FROM {table} ORDER BY id", null, Ct);

        Assert.Equal(expected, rows.Count);
        Assert.Equal(Enumerable.Range(1, expected).Select(i => (long)i), rows.Select(r => r.Id));
    }

    [Fact]
    public async Task Cardinality_operations_follow_the_contract_table()
    {
        await using var session = Session();
        const string none = "SELECT id, name FROM rows100 WHERE id < 0";
        const string one = "SELECT id, name FROM rows100 WHERE id = 7";
        const string many = "SELECT id, name FROM rows100 WHERE id <= 2 ORDER BY id";

        Assert.Empty(await session.QueryAsync<IdName>(none, null, Ct));
        await Assert.ThrowsAsync<VelQueryNoRowsException>(() => session.QueryFirstAsync<IdName>(none, null, Ct));
        Assert.Null(await session.QueryFirstOrDefaultAsync<IdName>(none, null, Ct));
        Assert.Equal(1, (await session.QueryFirstAsync<IdName>(many, null, Ct)).Id);
        Assert.Equal(new IdName(7, "row-7"), await session.QuerySingleAsync<IdName>(one, null, Ct));
        await Assert.ThrowsAsync<VelQueryTooManyRowsException>(() => session.QuerySingleAsync<IdName>(many, null, Ct));
        await Assert.ThrowsAsync<VelQueryTooManyRowsException>(() => session.QuerySingleOrDefaultAsync<IdName>(many, null, Ct));
        Assert.Null(await session.QuerySingleOrDefaultAsync<IdName>(none, null, Ct));
    }

    [Fact]
    public async Task Scalar_and_execute_report_provider_values()
    {
        await using var session = Session();
        Assert.Equal(100L, await session.ExecuteScalarAsync<long>("SELECT count(*) FROM rows100", cancellationToken: Ct));
        // SQLite integers are 64-bit; narrowing to int is overflow-checked by the driver, not truncated.
        Assert.Equal(100, await session.ExecuteScalarAsync<int>("SELECT count(*) FROM rows100", cancellationToken: Ct));
        await Assert.ThrowsAsync<OverflowException>(() => session.ExecuteScalarAsync<int>("SELECT 1099511627776", cancellationToken: Ct));
        Assert.Null(await session.ExecuteScalarAsync<int?>("SELECT category FROM rows100 WHERE id = 10", cancellationToken: Ct));
        await Assert.ThrowsAsync<VelQueryNoRowsException>(() => session.ExecuteScalarAsync<int>("SELECT 1 WHERE 0", cancellationToken: Ct));
        Assert.Null(await session.ExecuteScalarAsync<int?>("SELECT 1 WHERE 0", cancellationToken: Ct));
        var nullEx = await Assert.ThrowsAsync<VelQueryMappingException>(() => session.ExecuteScalarAsync<int>("SELECT NULL", cancellationToken: Ct));
        Assert.Equal(typeof(int), nullEx.TargetType);

        var table = await CreateTableAsync("id INTEGER PRIMARY KEY");
        Assert.Equal(3, await session.ExecuteAsync($"INSERT INTO {table} VALUES (1), (2), (3)", cancellationToken: Ct));
        Assert.Equal(2, await session.ExecuteAsync($"UPDATE {table} SET id = id + 10 WHERE id <= 2", cancellationToken: Ct));
    }

    [Fact]
    public async Task Ddl_reports_the_drivers_stale_change_count()
    {
        var table = await CreateTableAsync("id INTEGER PRIMARY KEY");
        await using var session = Session();
        await using var transaction = await session.BeginTransactionAsync(cancellationToken: Ct);
        Assert.Equal(2, await session.ExecuteAsync($"INSERT INTO {table} VALUES (1), (2)", cancellationToken: Ct));

        // Microsoft.Data.Sqlite adds sqlite3_changes() for every non-read-only statement, and for DDL that is still the
        // previous DML's count on the connection. VelQuery passes the driver's value through unchanged (it never invents
        // counts), so affected-row counts of DDL statements are meaningless on SQLite.
        Assert.Equal(2, await session.ExecuteAsync($"CREATE INDEX ix_{table} ON {table} (id)", cancellationToken: Ct));
    }

    [Theory]
    [InlineData("' OR 1=1 --")]
    [InlineData("'; DROP TABLE victims; --")]
    [InlineData("x'); ATTACH DATABASE 'pwned.db' AS pwned; --")]
    [InlineData("\"; DELETE FROM victims; --")]
    public async Task Parameter_values_are_data_not_sql(string payload)
    {
        await using var session = Session();

        var rows = await session.QueryAsync<IdName>("SELECT id, name FROM victims WHERE name = @Name", new QueryParameters().Add("Name", payload), Ct);

        Assert.Empty(rows);
        Assert.Equal(2L, await session.ExecuteScalarAsync<long>("SELECT count(*) FROM victims", cancellationToken: Ct));
        Assert.Equal(0L, await session.ExecuteScalarAsync<long>("SELECT count(*) FROM pragma_database_list WHERE name = 'pwned'", cancellationToken: Ct));
    }

    [Fact]
    public async Task Parameter_names_are_case_sensitive_and_unbound_placeholders_fail()
    {
        await using var session = Session();

        Assert.Equal(7L, await session.ExecuteScalarAsync<long>("SELECT @Id", new QueryParameters().Add("Id", 7L), Ct));
        // Unlike SQL Server, SQLite matches parameter names case-sensitively, and Microsoft.Data.Sqlite refuses to run
        // with an unbound placeholder (InvalidOperationException, not a DbException) instead of binding NULL silently.
        // Generated contracts are unaffected: they bind the placeholder spelling found in the SQL.
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteScalarAsync<long>("SELECT @id", new QueryParameters().Add("Id", 7L), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteScalarAsync<long>("SELECT @Missing", null, Ct));
    }

    public sealed record StrictCategory(long Id, int Category);

    [Fact]
    public async Task Strict_mapping_rejects_null_into_non_nullable_and_missing_columns()
    {
        await using var session = Session();

        var nullEx = await Assert.ThrowsAsync<VelQueryMappingException>(() =>
            session.QueryAsync<StrictCategory>("SELECT id, category FROM rows100 WHERE id = 10", null, Ct));
        var missing = await Assert.ThrowsAsync<VelQueryMappingException>(() => session.QueryAsync<IdName>("SELECT id FROM rows100", null, Ct));

        Assert.Equal("Category", nullEx.Column);
        Assert.Equal("Name", missing.Column);
    }

    [Fact]
    public async Task Multiple_result_sets_are_read_in_order_and_later_errors_surface()
    {
        await using var session = Session();
        await using (var multi = await session.QueryMultipleAsync(
            "SELECT id, name FROM rows100 WHERE id = 1; SELECT count(*) FROM rows100k", new QueryParameters(), Ct))
        {
            Assert.Equal(new IdName(1, "row-1"), await multi.ReadSingleAsync<IdName>(Ct));
            Assert.Equal(SqliteFixture.LargeRowCount, await multi.ReadSingleAsync<long>(Ct));
        }

        // SQLite returns NULL for 1 / 0; integer overflow is a real runtime error in a later statement.
        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => session.QueryAsync<long>("SELECT 1; SELECT abs(-9223372036854775808)", null, Ct));
        Assert.Equal(1, ((SqliteException)ex.InnerException!).SqliteErrorCode);
    }

    [Fact]
    public async Task Non_query_statements_in_a_batch_all_run()
    {
        var table = await CreateTableAsync("id INTEGER PRIMARY KEY");
        await using var session = Session();

        await session.ExecuteAsync($"INSERT INTO {table} VALUES (1); INSERT INTO {table} VALUES (2); UPDATE {table} SET id = id * 10", cancellationToken: Ct);

        Assert.Equal(30L, await session.ExecuteScalarAsync<long>($"SELECT sum(id) FROM {table}", cancellationToken: Ct));
    }

    [Fact]
    public async Task Syntax_errors_and_constraint_violations_are_provider_errors_with_the_sqlite_code()
    {
        await using var session = Session();

        var syntax = await Assert.ThrowsAsync<VelQueryProviderException>(() => session.ExecuteAsync("SELEC 1", cancellationToken: Ct));
        var duplicate = await Assert.ThrowsAsync<VelQueryProviderException>(() => session.ExecuteAsync("INSERT INTO victims VALUES (1, 'again')", cancellationToken: Ct));

        Assert.Equal(1, ((SqliteException)syntax.InnerException!).SqliteErrorCode);
        Assert.Equal(19, ((SqliteException)duplicate.InnerException!).SqliteErrorCode); // SQLITE_CONSTRAINT
        Assert.DoesNotContain("again", duplicate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stored_procedures_are_not_supported_by_sqlite()
    {
        await using var session = Session();

        Assert.False(session.Provider.Capabilities.HasFlag(Providers.ProviderCapabilities.StoredProcedures));
        await Assert.ThrowsAnyAsync<Exception>(() => session.Sql("anything").AsStoredProcedure().ExecuteAsync(Ct));
    }

    [Fact]
    public async Task Plain_memory_databases_are_per_connection_so_pin_one_or_use_a_shared_cache()
    {
        // Each VelQuery operation gets its own connection, and ":memory:" is a new empty database per connection.
        await using (var perConnection = VelQuerySqlite.CreateDataSource("Data Source=:memory:"))
        await using (var session = perConnection.CreateSession())
        {
            await session.ExecuteAsync("CREATE TABLE gone (id INTEGER)", cancellationToken: Ct);
            await Assert.ThrowsAsync<VelQueryProviderException>(() => session.ExecuteScalarAsync<long>("SELECT count(*) FROM gone", cancellationToken: Ct));

            // A session transaction pins one connection, so the database lives for the transaction.
            await using var transaction = await session.BeginTransactionAsync(cancellationToken: Ct);
            await session.ExecuteAsync("CREATE TABLE kept (id INTEGER); INSERT INTO kept VALUES (1)", cancellationToken: Ct);
            Assert.Equal(1L, await session.ExecuteScalarAsync<long>("SELECT count(*) FROM kept", cancellationToken: Ct));
        }

        // A named shared-cache memory database lives while at least one connection is open.
        var shared = $"Data Source=vq-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var keepAlive = new SqliteConnection(shared);
        await keepAlive.OpenAsync(Ct);
        await using var dataSource = VelQuerySqlite.CreateDataSource(shared);
        await using (var session = dataSource.CreateSession())
        {
            await session.ExecuteAsync("CREATE TABLE kept (id INTEGER); INSERT INTO kept VALUES (1), (2)", cancellationToken: Ct);
            Assert.Equal(2L, await session.ExecuteScalarAsync<long>("SELECT count(*) FROM kept", cancellationToken: Ct));
        }
    }

    [Fact]
    public void Provider_dialect_quotes_identifiers_and_formats_parameters()
    {
        var provider = SqliteProvider.Instance;

        Assert.Equal("sqlite", provider.DialectId);
        Assert.Equal("@Id", provider.FormatParameterName("Id"));
        Assert.Equal("\"we\"\"ird\"", provider.QuoteIdentifier("we\"ird"));
        Assert.Throws<ArgumentException>(() => provider.QuoteIdentifier(""));
        Assert.Throws<ArgumentException>(() => provider.QuoteIdentifier("a\0b"));
        Assert.True(provider.Capabilities.HasFlag(Providers.ProviderCapabilities.NativeConversions));
    }

    [Fact]
    public async Task Provider_db_type_selects_the_sqlite_storage_class()
    {
        await using var session = Session();
        var guid = Guid.NewGuid();

        var asText = await session.ExecuteScalarAsync<string>("SELECT typeof(@G)", new QueryParameters().Add("G", guid), Ct);
        var asBlob = await session.ExecuteScalarAsync<string>("SELECT typeof(@G)",
            new QueryParameters().Add(new Parameters.ParameterSpec("G", DbType.Guid, ProviderDbType: (int)SqliteType.Blob), guid), Ct);
        var roundTrip = await session.ExecuteScalarAsync<Guid>("SELECT @G",
            new QueryParameters().Add(new Parameters.ParameterSpec("G", DbType.Guid, ProviderDbType: (int)SqliteType.Blob), guid), Ct);

        Assert.Equal("text", asText);
        Assert.Equal("blob", asBlob);
        Assert.Equal(guid, roundTrip);
    }
}
