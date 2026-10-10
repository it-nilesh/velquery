using System;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Core.UnitTests.Fakes;
using Xunit;

namespace VelQuery.Core.UnitTests;

/// <summary>PRD §9.1: cleanup faults never hide the initiating failure, and surface when nothing else failed.</summary>
public sealed class CleanupFailureTests
{
    private const string Sql = "select v from t";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<Op> AllOps => Operations.AllData();

    public static TheoryData<Op> ReaderOps => Operations.ReaderData();

    public static TheoryData<Op> FullReadOps => Operations.FullReadData();

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Execute_fault_wins_over_command_and_connection_dispose_faults(Op op)
    {
        var h = new Harness();
        var original = new FakeDbException("execute failed");
        h.Db.Script(Sql, new FakeScript { ExecuteFault = original, CommandDisposeFault = new FakeDbException("command dispose failed") }
            .With(FakeResultSet.Ints("v", 1)));
        h.Db.ConnectionDisposeFault = new FakeDbException("connection dispose failed");
        await using var session = h.Session();

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => Operations.RunAsync(session, op, Sql, Ct));

        Assert.Same(original, ex.InnerException);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Theory]
    [MemberData(nameof(FullReadOps))]
    public async Task Read_fault_wins_over_reader_and_command_dispose_faults(Op op)
    {
        var h = new Harness();
        var original = new FakeDbException("read failed");
        h.Db.Script(Sql, new FakeScript
        {
            ReadFault = original,
            ReadFaultAt = 2,
            ReaderDisposeFault = new FakeDbException("reader dispose failed"),
            CommandDisposeFault = new FakeDbException("command dispose failed"),
        }.With(FakeResultSet.Ints("v", 5)));
        await using var session = h.Session();

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => Operations.RunAsync(session, op, Sql, Ct));

        Assert.Same(original, ex.InnerException);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Mapper_failure_wins_over_reader_dispose_fault()
    {
        var h = new Harness();
        h.Db.Script(Sql, new FakeScript { ReaderDisposeFault = new FakeDbException("reader dispose failed") }.With(FakeResultSet.Ints("v", 100)));
        await using var session = h.Session();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.QueryAsync(new QueryDescriptor<NoArgs, int>("rows", Sql, new ThrowingAtRowFactory(3)), new NoArgs(), Ct));

        Assert.Contains("row 3", ex.Message, StringComparison.Ordinal);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Stream_consumer_exception_wins_over_reader_and_command_dispose_faults()
    {
        var h = new Harness();
        h.Db.Script(Sql, new FakeScript
        {
            ReaderDisposeFault = new FakeDbException("reader dispose failed"),
            CommandDisposeFault = new FakeDbException("command dispose failed"),
        }.With(FakeResultSet.Ints("v", 100)));
        await using var session = h.Session();
        var consumerError = new InvalidOperationException("consumer failed");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in session.StreamAsync<int>(Sql, null, Ct))
            {
                throw consumerError;
            }
        });

        Assert.Same(consumerError, ex);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Cancellation_wins_over_dispose_faults()
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        h.Db.Script(Sql, new FakeScript
        {
            OnRead = n =>
            {
                if (n == 2)
                {
                    cts.Cancel();
                }
            },
            ReaderDisposeFault = new FakeDbException("reader dispose failed"),
            CommandDisposeFault = new FakeDbException("command dispose failed"),
        }.With(FakeResultSet.Ints("v", 100)));
        await using var session = h.Session();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.QueryAsync<int>(Sql, null, cts.Token));
        h.Db.AssertAllReleasedExactlyOnce();
    }

    // ---- Success path: nothing else failed, so the cleanup failure must surface (as a provider failure). ----

    [Theory]
    [MemberData(nameof(ReaderOps))]
    public async Task Reader_dispose_fault_on_success_surfaces_and_everything_is_still_released(Op op)
    {
        var h = new Harness();
        var fault = new FakeDbException("reader dispose failed");
        h.Db.Script(Sql, new FakeScript { ReaderDisposeFault = fault }.With(FakeResultSet.Ints("v", 1)));
        await using var session = h.Session();

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => Operations.RunAsync(session, op, Sql, Ct));

        Assert.Same(fault, ex.InnerException);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Command_dispose_fault_on_success_surfaces_and_everything_is_still_released(Op op)
    {
        var h = new Harness();
        var fault = new FakeDbException("command dispose failed");
        h.Db.Script(Sql, new FakeScript { CommandDisposeFault = fault }.With(FakeResultSet.Ints("v", 1)));
        await using var session = h.Session();

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => Operations.RunAsync(session, op, Sql, Ct));

        Assert.Same(fault, ex.InnerException);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Connection_dispose_fault_on_success_surfaces(Op op)
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Ints("v", 1));
        var fault = new FakeDbException("connection dispose failed");
        h.Db.ConnectionDisposeFault = fault;
        await using var session = h.Session();

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => Operations.RunAsync(session, op, Sql, Ct));

        Assert.Same(fault, ex.InnerException);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Non_provider_cleanup_failure_on_success_surfaces_unchanged()
    {
        var h = new Harness();
        var fault = new InvalidOperationException("command dispose failed");
        h.Db.Script(Sql, new FakeScript { CommandDisposeFault = fault, RecordsAffected = 1 });
        await using var session = h.Session();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(Sql, null, Ct));

        Assert.Same(fault, ex);
        h.Db.AssertAllReleasedExactlyOnce();
    }
}
