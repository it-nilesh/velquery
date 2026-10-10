using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Core.UnitTests.Fakes;
using VelQuery.Mapping;
using Xunit;

namespace VelQuery.Core.UnitTests;

/// <summary>Regression tests for defects reported by the benchmark and unit-test passes.</summary>
public sealed class FixupRegressionTests
{
    private const string Sql = "select v from t";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Driver_date_and_time_values_convert_losslessly_to_DateOnly_and_TimeOnly()
    {
        Assert.Equal(new DateOnly(2026, 10, 10), ValueConverter.Convert<DateOnly>(new DateTime(2026, 10, 10), "q", "d", strictNulls: true));
        Assert.Equal(new TimeOnly(13, 45, 7), ValueConverter.Convert<TimeOnly>(new TimeSpan(13, 45, 7), "q", "t", strictNulls: true));
        Assert.Equal('x', ValueConverter.Convert<char>("x", "q", "c", strictNulls: true));
    }

    [Fact]
    public void Lossy_date_time_and_char_conversions_are_rejected()
    {
        Assert.Throws<VelQueryMappingException>(() => ValueConverter.Convert<DateOnly>(new DateTime(2026, 10, 10, 1, 0, 0), "q", "d", true));
        Assert.Throws<VelQueryMappingException>(() => ValueConverter.Convert<TimeOnly>(TimeSpan.FromHours(25), "q", "t", true));
        Assert.Throws<VelQueryMappingException>(() => ValueConverter.Convert<char>("xy", "q", "c", true));
    }

    public sealed class TiedConstructors(int id, string name)
    {
        public TiedConstructors(string name, int id)
            : this(id, name)
        {
        }

        public int Id { get; } = id;

        public string Name { get; } = name;
    }

    [Fact]
    public async Task Reflection_mapper_rejects_tied_widest_constructors_like_the_generator()
    {
        var h = Harness.WithFallback();
        h.Script("select Id, Name from t", new FakeResultSet(("Id", typeof(int)), ("Name", typeof(string))).Row(1, "a"));
        await using var session = h.Session();

        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => session.QueryAsync<TiedConstructors>("select Id, Name from t", null, Ct));
        Assert.Contains("widest", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Descriptor_WithCommandTimeout_overrides_session_timeout_for_one_call()
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Ints("v", 1));
        var descriptor = Descriptors.Scalar<int>(Sql);
        await using var session = h.Session();
        session.CommandTimeout = TimeSpan.FromSeconds(22);

        await session.QueryAsync(descriptor.WithCommandTimeout(TimeSpan.FromSeconds(5)), new NoArgs(), Ct);
        Assert.Equal(5, h.Db.Commands[^1].TimeoutAtExecute);

        await session.QueryAsync(descriptor, new NoArgs(), Ct);
        Assert.Equal(22, h.Db.Commands[^1].TimeoutAtExecute);
        Assert.Null(descriptor.CommandTimeout);
    }

    [Fact]
    public async Task Synchronous_session_dispose_rolls_back_and_releases_the_pinned_connection()
    {
        var h = new Harness();
        var session = h.Session();
        await session.BeginTransactionAsync(cancellationToken: Ct);

        session.Dispose();

        Assert.True(h.Db.Transactions[0].WasRolledBack);
        h.Db.AssertAllReleasedExactlyOnce();
        await session.DisposeAsync();
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Synchronous_session_dispose_never_touches_caller_owned_resources()
    {
        var h = new Harness();
        var connection = h.Db.OpenCallerConnection();
        var transaction = await connection.BeginTransactionAsync(Ct);
        var session = h.DataSource.CreateSession(connection, transaction);

        session.Dispose();

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        Assert.False(h.Db.Transactions[0].WasRolledBack);
    }

    private sealed class TrackingDataSource : DbDataSource
    {
        public bool Disposed { get; private set; }

        public override string ConnectionString => "fake";

        protected override DbConnection CreateDbConnection() => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Data_source_disposes_driver_data_source_only_when_owned(bool owned)
    {
        var driver = new TrackingDataSource();
        var dataSource = new VelQueryDataSource(new FakeProvider(), new DataSourceConnectionFactory(driver, owned));

        await dataSource.DisposeAsync();

        Assert.Equal(owned, driver.Disposed);
    }
}
