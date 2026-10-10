using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Core.UnitTests.Fakes;
using Xunit;

namespace VelQuery.Core.UnitTests;

/// <summary>VQ-FR-001, VQ-FR-009, VQ-FR-013, VQ-FR-027 and PRD §9.1/§18.1: every owned handle released exactly once.</summary>
public sealed class OwnershipTests
{
    private const string Sql = "select v from t";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<Op> AllOps => Operations.AllData();

    public static TheoryData<Op> FullReadOps => Operations.FullReadData();

    private static Harness OneRow()
    {
        var harness = new Harness();
        harness.Script(Sql, FakeResultSet.Ints("v", 1)).RecordsAffected = 1;
        return harness;
    }

    private static Harness TwoSets()
    {
        var harness = new Harness();
        harness.Script(Sql, FakeResultSet.Ints("v", 5), FakeResultSet.Ints("v", 2));
        return harness;
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Success_disposes_factory_connection_command_and_reader_exactly_once(Op op)
    {
        var h = OneRow();
        await using (var session = h.Session())
        {
            await Operations.RunAsync(session, op, Sql, Ct);
        }

        var connection = Assert.Single(h.Db.Connections);
        Assert.True(connection.IsFactoryOwned);
        h.Db.AssertAllReleasedExactlyOnce();
        Operations.AssertNotCancelled(h.Db);
        Assert.Equal(Operations.UsesReader(op) ? 1 : 0, h.Db.Readers.Count);
    }

    [Fact]
    public async Task Each_operation_on_a_factory_session_uses_its_own_connection()
    {
        var h = OneRow();
        await using var session = h.Session();
        await session.QueryAsync<int>(Sql, null, Ct);
        await session.ExecuteAsync(Sql, null, Ct);
        await session.ExecuteScalarAsync<int>(Sql, null, Ct);

        Assert.Equal(3, h.Db.Connections.Count);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public void Creating_sessions_and_streams_performs_no_io()
    {
        var h = OneRow();
        var session = h.Session();
        _ = session.StreamAsync<int>(Sql, null, Ct);
        _ = session.Sql(Sql).WithTimeout(TimeSpan.FromSeconds(1)).StreamAsync<int>(Ct);

        Assert.Empty(h.Db.Connections);
        Assert.Empty(h.Db.Commands);
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Open_fault_releases_connection_and_creates_no_command(Op op)
    {
        var h = OneRow();
        var fault = new FakeDbException("open failed");
        h.Db.ConnectionOpenFault = fault;
        await using var session = h.Session();

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => Operations.RunAsync(session, op, Sql, Ct));

        Assert.Same(fault, ex.InnerException);
        Assert.Equal(1, Assert.Single(h.Db.Connections).DisposeCount);
        Assert.Empty(h.Db.Commands);
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Execute_fault_releases_everything(Op op)
    {
        var h = OneRow();
        var fault = new FakeDbException("execute failed");
        h.Db.Script(Sql, new FakeScript { ExecuteFault = fault }.With(FakeResultSet.Ints("v", 1)));
        await using var session = h.Session();

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => Operations.RunAsync(session, op, Sql, Ct));

        Assert.Same(fault, ex.InnerException);
        Assert.Equal(VelQueryErrorCategory.Provider, ex.Category);
        h.Db.AssertAllReleasedExactlyOnce();
        Assert.Single(h.Db.Connections);
        Assert.Empty(h.Db.Readers);
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Pre_cancelled_token_acquires_nothing(Op op)
    {
        var h = OneRow();
        await using var session = h.Session();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Operations.RunAsync(session, op, Sql, cts.Token));

        Assert.Empty(h.Db.Connections);
        Assert.Empty(h.Db.Commands);
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Cancellation_during_connect_releases_connection(Op op)
    {
        var h = OneRow();
        using var cts = new CancellationTokenSource();
        h.Db.OnConnectionOpen = cts.Cancel;
        await using var session = h.Session();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Operations.RunAsync(session, op, Sql, cts.Token));

        Assert.Equal(1, Assert.Single(h.Db.Connections).DisposeCount);
        Assert.Empty(h.Db.Commands);
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Cancellation_during_execute_releases_everything(Op op)
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        h.Db.Script(Sql, new FakeScript { OnExecute = _ => cts.Cancel() }.With(FakeResultSet.Ints("v", 1)));
        await using var session = h.Session();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Operations.RunAsync(session, op, Sql, cts.Token));

        h.Db.AssertAllReleasedExactlyOnce();
        Assert.Single(h.Db.Connections);
        Assert.Single(h.Db.Commands);
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Provider_error_raised_because_of_cancellation_surfaces_as_cancellation(Op op)
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        var providerError = new FakeDbException("operation cancelled by user");
        h.Db.Script(Sql, new FakeScript
        {
            OnExecute = _ =>
            {
                cts.Cancel();
                throw providerError;
            },
        }.With(FakeResultSet.Ints("v", 1)));
        await using var session = h.Session();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Operations.RunAsync(session, op, Sql, cts.Token));

        Assert.Same(providerError, ex.InnerException);
        Assert.Equal(cts.Token, ex.CancellationToken);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Theory]
    [MemberData(nameof(FullReadOps))]
    public async Task Read_fault_cancels_command_and_releases_everything(Op op)
    {
        var h = TwoSets();
        var fault = new FakeDbException("read failed");
        h.Db.Script(Sql, new FakeScript { ReadFault = fault, ReadFaultAt = 3 }.With(FakeResultSet.Ints("v", 5)));
        await using var session = h.Session();

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => Operations.RunAsync(session, op, Sql, Ct));

        Assert.Same(fault, ex.InnerException);
        h.Db.AssertAllReleasedExactlyOnce();
        Operations.AssertCancelledBeforeReaderDispose(h.Db);
        Assert.Equal(3, Assert.Single(h.Db.Readers).ReadCalls);
    }

    [Theory]
    [MemberData(nameof(FullReadOps))]
    public async Task NextResult_fault_releases_everything(Op op)
    {
        var h = new Harness();
        var fault = new FakeDbException("next result failed");
        h.Db.Script(Sql, new FakeScript { NextResultFault = fault }.With(FakeResultSet.Ints("v", 2), FakeResultSet.Ints("v", 2)));
        await using var session = h.Session();

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => Operations.RunAsync(session, op, Sql, Ct));

        Assert.Same(fault, ex.InnerException);
        h.Db.AssertAllReleasedExactlyOnce();
        Operations.AssertCancelledBeforeReaderDispose(h.Db);
    }

    [Theory]
    [MemberData(nameof(FullReadOps))]
    public async Task Cancellation_during_read_cancels_command_and_releases_everything(Op op)
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        h.Db.Script(Sql, new FakeScript
        {
            OnRead = n =>
            {
                if (n == 3)
                {
                    cts.Cancel();
                }
            },
        }.With(FakeResultSet.Ints("v", 10)));
        await using var session = h.Session();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Operations.RunAsync(session, op, Sql, cts.Token));

        h.Db.AssertAllReleasedExactlyOnce();
        Operations.AssertCancelledBeforeReaderDispose(h.Db);
        Assert.Equal(3, Assert.Single(h.Db.Readers).ReadCalls);
    }

    [Fact]
    public async Task Stream_early_break_cancels_then_disposes_reader_command_and_connection()
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Ints("v", 1000), FakeResultSet.Ints("v", 3));
        await using var session = h.Session();

        var seen = new List<int>();
        await foreach (var value in session.StreamAsync<int>(Sql, null, Ct))
        {
            seen.Add(value);
            break;
        }

        Assert.Equal([1], seen);
        h.Db.AssertAllReleasedExactlyOnce();
        Operations.AssertCancelledBeforeReaderDispose(h.Db);
        var reader = Assert.Single(h.Db.Readers);
        Assert.Equal(1, reader.ReadCalls);
        Assert.Equal(0, reader.NextResultCalls);
    }

    [Fact]
    public async Task Stream_holds_connection_until_enumeration_completes()
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Ints("v", 3));
        await using var session = h.Session();

        var enumerator = session.StreamAsync<int>(Sql, null, Ct).GetAsyncEnumerator(Ct);
        try
        {
            Assert.True(await enumerator.MoveNextAsync());
            var connection = Assert.Single(h.Db.Connections);
            Assert.Equal(0, connection.DisposeCount);
            Assert.Equal(ConnectionState.Open, connection.State);

            while (await enumerator.MoveNextAsync())
            {
            }

            Assert.Equal(1, connection.DisposeCount);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        h.Db.AssertAllReleasedExactlyOnce();
        Operations.AssertNotCancelled(h.Db);
    }

    [Fact]
    public async Task Stream_consumer_exception_releases_everything_and_propagates_unchanged()
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Ints("v", 100));
        await using var session = h.Session();
        var consumerError = new InvalidOperationException("consumer failed");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var value in session.StreamAsync<int>(Sql, null, Ct))
            {
                if (value == 2)
                {
                    throw consumerError;
                }
            }
        });

        Assert.Same(consumerError, ex);
        h.Db.AssertAllReleasedExactlyOnce();
        Operations.AssertCancelledBeforeReaderDispose(h.Db);
    }

    [Fact]
    public async Task Stream_cancelled_through_WithCancellation_releases_everything()
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Ints("v", 100));
        await using var session = h.Session();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var value in session.StreamAsync<int>(Sql, null, Ct).WithCancellation(cts.Token))
            {
                if (value == 5)
                {
                    await cts.CancelAsync();
                }
            }
        });

        h.Db.AssertAllReleasedExactlyOnce();
        Operations.AssertCancelledBeforeReaderDispose(h.Db);
    }

    [Fact]
    public async Task Stream_can_be_enumerated_twice_and_executes_once_per_enumeration()
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Ints("v", 3));
        await using var session = h.Session();
        var stream = session.StreamAsync<int>(Sql, null, Ct);

        var first = new List<int>();
        await foreach (var v in stream)
        {
            first.Add(v);
        }

        var second = new List<int>();
        await foreach (var v in stream)
        {
            second.Add(v);
        }

        Assert.Equal([1, 2, 3], first);
        Assert.Equal(first, second);
        Assert.Equal(2, h.Db.Connections.Count);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Mapper_throwing_at_row_37_of_1000_while_buffering_releases_everything()
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Ints("v", 1000));
        var factory = new ThrowingAtRowFactory(37);
        await using var session = h.Session();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.QueryAsync(new QueryDescriptor<NoArgs, int>("rows.throwing", Sql, factory), new NoArgs(), Ct));

        Assert.Contains("row 37", ex.Message, StringComparison.Ordinal);
        Assert.Equal(37, Assert.Single(h.Db.Readers).ReadCalls);
        h.Db.AssertAllReleasedExactlyOnce();
        Operations.AssertCancelledBeforeReaderDispose(h.Db);
    }

    [Fact]
    public async Task Mapper_throwing_at_row_37_of_1000_while_streaming_releases_everything()
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Ints("v", 1000));
        var factory = new ThrowingAtRowFactory(37);
        await using var session = h.Session();
        var consumed = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in session.StreamAsync(new QueryDescriptor<NoArgs, int>("rows.throwing", Sql, factory), new NoArgs(), Ct))
            {
                consumed++;
            }
        });

        Assert.Equal(36, consumed);
        Assert.Equal(37, Assert.Single(h.Db.Readers).ReadCalls);
        h.Db.AssertAllReleasedExactlyOnce();
        Operations.AssertCancelledBeforeReaderDispose(h.Db);
    }

    [Fact]
    public async Task Mapper_throwing_at_row_37_of_1000_in_multi_result_releases_everything()
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Ints("v", 1000), FakeResultSet.Ints("v", 1));
        var factory = new ThrowingAtRowFactory(37);
        await using (var session = h.Session())
        {
            await using var multi = await session.QueryMultipleAsync(Sql, null, Ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => multi.ReadAsync(factory, Ct));
        }

        h.Db.AssertAllReleasedExactlyOnce();
        Operations.AssertCancelledBeforeReaderDispose(h.Db);
    }

    // ---- Caller-owned connections and transactions (VQ-FR-001, VQ-FR-011) ----

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Caller_owned_connection_is_never_closed_or_disposed(Op op)
    {
        var h = OneRow();
        using var connection = h.Db.OpenCallerConnection();
        await using (var session = h.DataSource.CreateSession(connection))
        {
            await Operations.RunAsync(session, op, Sql, Ct);
            await Operations.RunAsync(session, op, Sql, Ct);
        }

        Assert.Equal(0, connection.DisposeCount);
        Assert.Equal(0, connection.CloseCount);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Single(h.Db.Connections);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Caller_owned_connection_survives_faults_and_cancellation(Op op)
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        h.Db.Script("fail", new FakeScript { ExecuteFault = new FakeDbException() }.With(FakeResultSet.Ints("v", 1)));
        h.Db.Script("cancel", new FakeScript { OnExecute = _ => cts.Cancel() }.With(FakeResultSet.Ints("v", 1)));
        h.Db.Script("ok", FakeResultSet.Ints("v", 1));
        using var connection = h.Db.OpenCallerConnection();
        await using var session = h.DataSource.CreateSession(connection);

        await Assert.ThrowsAsync<VelQueryProviderException>(() => Operations.RunAsync(session, op, "fail", Ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Operations.RunAsync(session, op, "cancel", cts.Token));
        await Operations.RunAsync(session, op, "ok", Ct);

        Assert.Equal(0, connection.DisposeCount);
        Assert.Equal(0, connection.CloseCount);
        Assert.Equal(ConnectionState.Open, connection.State);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Caller_owned_transaction_is_attached_to_every_command_and_never_completed(Op op)
    {
        var h = OneRow();
        h.Db.Script("fail", new FakeScript { ExecuteFault = new FakeDbException() }.With(FakeResultSet.Ints("v", 1)));
        using var connection = h.Db.OpenCallerConnection();
        using var transaction = connection.BeginTransaction();
        await using (var session = h.DataSource.CreateSession(connection, transaction))
        {
            Assert.Same(transaction, session.Transaction);
            await Operations.RunAsync(session, op, Sql, Ct);
            await Assert.ThrowsAsync<VelQueryProviderException>(() => Operations.RunAsync(session, op, "fail", Ct));
            await Operations.RunAsync(session, op, Sql, Ct);
        }

        Assert.Equal(3, h.Db.Commands.Count);
        Assert.All(h.Db.Commands, c => Assert.Same(transaction, c.TransactionAtExecute));
        var fake = Assert.Single(h.Db.Transactions);
        Assert.Equal(0, fake.CommitCount);
        Assert.False(fake.WasRolledBack);
        Assert.Equal(0, fake.DisposeCount);
        Assert.Equal(0, connection.DisposeCount);
    }

    [Fact]
    public void Caller_owned_connection_must_be_open_and_own_the_transaction()
    {
        var h = new Harness();
        var closed = h.Db.OpenCallerConnection();
        closed.Close();
        Assert.Throws<VelQueryValidationException>(() => h.DataSource.CreateSession(closed));

        using var a = h.Db.OpenCallerConnection();
        using var b = h.Db.OpenCallerConnection();
        using var foreign = b.BeginTransaction();
        Assert.Throws<VelQueryValidationException>(() => h.DataSource.CreateSession(a, foreign));
    }

    [Fact]
    public void Factory_session_requires_a_connection_factory()
    {
        var dataSource = new VelQueryDataSource(new FakeProvider(), connectionFactory: null);
        Assert.Throws<VelQueryValidationException>(() => dataSource.CreateSession());
    }
}
