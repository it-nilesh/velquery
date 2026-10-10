using System;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Core.UnitTests.Fakes;
using Xunit;

namespace VelQuery.Core.UnitTests;

/// <summary>VQ-FR-010 timeout precedence/validation and VQ-FR-026 failure classification.</summary>
public sealed class TimeoutAndErrorTests
{
    private const string Sql = "select v from t";
    private const int ProviderDefault = 30;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<Op> AllOps => Operations.AllData();

    private static Harness Create(TimeSpan? defaultTimeout = null)
    {
        var h = new Harness(new VelQueryOptions { DefaultTimeout = defaultTimeout });
        h.Script(Sql, FakeResultSet.Ints("v", 1));
        return h;
    }

    private static async Task<int> TimeoutOf(Harness h, Func<VelQuerySession, Task> run, TimeSpan? sessionTimeout = null)
    {
        await using var session = h.Session();
        session.CommandTimeout = sessionTimeout;
        await run(session);
        return h.Db.Commands[^1].TimeoutAtExecute;
    }

    [Fact]
    public async Task Runtime_sql_precedence_is_per_call_then_session_then_options_then_provider()
    {
        Task Plain(VelQuerySession s) => s.QueryAsync<int>(Sql, null, Ct);
        Task PerCall(VelQuerySession s) => s.Sql(Sql).WithTimeout(TimeSpan.FromSeconds(44)).QueryAsync<int>(Ct);

        Assert.Equal(44, await TimeoutOf(Create(TimeSpan.FromSeconds(11)), PerCall, TimeSpan.FromSeconds(22)));
        Assert.Equal(22, await TimeoutOf(Create(TimeSpan.FromSeconds(11)), Plain, TimeSpan.FromSeconds(22)));
        Assert.Equal(11, await TimeoutOf(Create(TimeSpan.FromSeconds(11)), Plain));
        Assert.Equal(ProviderDefault, await TimeoutOf(Create(), Plain));
    }

    [Fact]
    public async Task Descriptor_precedence_is_descriptor_then_session_then_options_then_provider()
    {
        Task WithDescriptorTimeout(VelQuerySession s) => s.QueryAsync(Descriptors.Scalar<int>(Sql, timeout: TimeSpan.FromSeconds(33)), new NoArgs(), Ct);
        Task Plain(VelQuerySession s) => s.QueryAsync(Descriptors.Scalar<int>(Sql), new NoArgs(), Ct);

        Assert.Equal(33, await TimeoutOf(Create(TimeSpan.FromSeconds(11)), WithDescriptorTimeout, TimeSpan.FromSeconds(22)));
        Assert.Equal(22, await TimeoutOf(Create(TimeSpan.FromSeconds(11)), Plain, TimeSpan.FromSeconds(22)));
        Assert.Equal(11, await TimeoutOf(Create(TimeSpan.FromSeconds(11)), Plain));
        Assert.Equal(ProviderDefault, await TimeoutOf(Create(), Plain));
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Every_operation_applies_the_resolved_timeout(Op op)
    {
        var h = Create(TimeSpan.FromSeconds(11));
        await using var session = h.Session();
        session.CommandTimeout = TimeSpan.FromSeconds(7);

        await Operations.RunAsync(session, op, Sql, Ct);

        Assert.Equal(7, Assert.Single(h.Db.Commands).TimeoutAtExecute);
    }

    [Fact]
    public async Task Infinite_timeout_maps_to_zero_at_every_level()
    {
        Task PerCall(VelQuerySession s) => s.Sql(Sql).WithTimeout(Timeout.InfiniteTimeSpan).ExecuteAsync(Ct);
        Task Plain(VelQuerySession s) => s.ExecuteAsync(Sql, null, Ct);
        Task Descriptor(VelQuerySession s) => s.ExecuteAsync(new QueryDescriptor<NoArgs>("x", Sql, commandTimeout: Timeout.InfiniteTimeSpan), new NoArgs(), Ct);

        Assert.Equal(0, await TimeoutOf(Create(TimeSpan.FromSeconds(11)), PerCall));
        Assert.Equal(0, await TimeoutOf(Create(TimeSpan.FromSeconds(11)), Plain, Timeout.InfiniteTimeSpan));
        Assert.Equal(0, await TimeoutOf(Create(Timeout.InfiniteTimeSpan), Plain));
        Assert.Equal(0, await TimeoutOf(Create(TimeSpan.FromSeconds(11)), Descriptor));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(500, 1)]
    [InlineData(1000, 1)]
    [InlineData(1001, 2)]
    [InlineData(1200, 2)]
    [InlineData(59_999, 60)]
    public async Task Partial_seconds_round_up(int milliseconds, int expectedSeconds)
    {
        Task PerCall(VelQuerySession s) => s.Sql(Sql).WithTimeout(TimeSpan.FromMilliseconds(milliseconds)).ExecuteAsync(Ct);
        Assert.Equal(expectedSeconds, await TimeoutOf(Create(), PerCall));
    }

    public static TheoryData<TimeSpan> InvalidTimeouts => new()
    {
        TimeSpan.Zero,
        TimeSpan.FromSeconds(-1),
        TimeSpan.FromMilliseconds(-2),
    };

    [Theory]
    [MemberData(nameof(InvalidTimeouts))]
    public async Task Zero_and_negative_timeouts_are_rejected_everywhere(TimeSpan timeout)
    {
        var h = Create();
        await using var session = h.Session();

        Assert.Throws<VelQueryValidationException>(() => session.Sql(Sql).WithTimeout(timeout));
        Assert.Throws<VelQueryValidationException>(() => session.CommandTimeout = timeout);
        Assert.Throws<VelQueryValidationException>(() => new VelQueryDataSource(new FakeProvider(), null, new VelQueryOptions { DefaultTimeout = timeout }));

        // Descriptors are constructed in Abstractions; an invalid timeout is rejected before any command executes.
        var ex = await Assert.ThrowsAsync<VelQueryValidationException>(() =>
            session.ExecuteAsync(new QueryDescriptor<NoArgs>("bad.timeout", Sql, commandTimeout: timeout), new NoArgs(), Ct));
        Assert.Equal("bad.timeout", ex.QueryId);
        Assert.All(h.Db.Commands, c => Assert.Null(c.ExecutedAs));
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Options_are_copied_when_the_data_source_is_created()
    {
        var options = new VelQueryOptions { DefaultTimeout = TimeSpan.FromSeconds(5) };
        var db = new FakeDatabase();
        db.Script(Sql, FakeResultSet.Ints("v", 1));
        var dataSource = new VelQueryDataSource(new FakeProvider(), new FakeConnectionFactory(db), options);
        options.DefaultTimeout = TimeSpan.FromSeconds(50);

        await using (var session = dataSource.CreateSession())
        {
            await session.ExecuteAsync(Sql, null, Ct);
        }

        Assert.Equal(5, Assert.Single(db.Commands).TimeoutAtExecute);
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Provider_timeout_becomes_VelQueryTimeoutException_with_original_inner_exception(Op op)
    {
        var h = new Harness();
        var timeout = new FakeDbException("timeout expired", isTimeout: true);
        h.Db.Script(Sql, new FakeScript { ExecuteFault = timeout }.With(FakeResultSet.Ints("v", 1)));
        await using var session = h.Session();

        var ex = await Assert.ThrowsAsync<VelQueryTimeoutException>(() => Operations.RunAsync(session, op, Sql, Ct));

        Assert.Same(timeout, ex.InnerException);
        Assert.Equal(VelQueryErrorCategory.Timeout, ex.Category);
        Assert.NotNull(ex.QueryId);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Provider_timeout_during_read_is_classified_as_timeout()
    {
        var h = new Harness();
        var timeout = new FakeDbException("timeout expired", isTimeout: true);
        h.Db.Script(Sql, new FakeScript { ReadFault = timeout, ReadFaultAt = 2 }.With(FakeResultSet.Ints("v", 3)));
        await using var session = h.Session();

        var ex = await Assert.ThrowsAsync<VelQueryTimeoutException>(async () =>
        {
            await foreach (var _ in session.StreamAsync<int>(Sql, null, Ct))
            {
            }
        });

        Assert.Same(timeout, ex.InnerException);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Generic_provider_error_becomes_VelQueryProviderException_with_query_id(Op op)
    {
        var h = new Harness();
        var error = new FakeDbException("deadlock victim");
        h.Db.Script(Sql, new FakeScript { ExecuteFault = error }.With(FakeResultSet.Ints("v", 1)));
        await using var session = h.Session();

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => Operations.RunAsync(session, op, Sql, Ct));

        Assert.Same(error, ex.InnerException);
        Assert.Contains(nameof(FakeDbException), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("deadlock victim", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_reported_cancellation_without_token_becomes_OperationCanceledException()
    {
        var h = new Harness();
        var cancelled = new FakeDbException("cancelled", isCancellation: true);
        h.Db.Script(Sql, new FakeScript { ExecuteFault = cancelled });
        await using var session = h.Session();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync(Sql, null, Ct));

        Assert.Same(cancelled, ex.InnerException);
    }

    [Fact]
    public async Task Non_provider_exceptions_pass_through_unchanged()
    {
        var h = new Harness();
        var bug = new InvalidOperationException("not a provider error");
        h.Db.Script(Sql, new FakeScript { ExecuteFault = bug });
        await using var session = h.Session();

        Assert.Same(bug, await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(Sql, null, Ct)));
    }

    [Fact]
    public async Task Caller_cancellation_surfaces_as_OperationCanceledException_with_the_callers_token()
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        h.Db.Script(Sql, new FakeScript { OnExecute = _ => cts.Cancel() });
        await using var session = h.Session();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync(Sql, null, cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
    }
}
