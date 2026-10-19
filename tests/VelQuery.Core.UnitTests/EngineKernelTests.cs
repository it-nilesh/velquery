using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Engine;
using VelQuery.Providers;
using Xunit;

namespace VelQuery.Core.UnitTests;

/// <summary>
/// Contract tests for the engine-neutral kernel (ADR-0016) using a non-ADO.NET fake engine, the way document or
/// key-value adapters will use it.
/// </summary>
public sealed class EngineKernelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class DriverException(string message) : Exception(message);

    private sealed class DriverTimeout() : Exception("driver timeout");

    private sealed class Classifier : IVelQueryErrorClassifier
    {
        public ProviderErrorKind Classify(Exception exception) => exception is DriverTimeout ? ProviderErrorKind.Timeout : ProviderErrorKind.Other;

        public bool IsProviderException(Exception exception) => exception is DriverException or DriverTimeout;
    }

    private static readonly Classifier s_classifier = new();

    private static Exception TranslateCaught(Exception exception, CancellationToken token)
    {
        try
        {
            throw VelQueryErrors.Translate(exception, "q.id", s_classifier, token);
        }
        catch (Exception thrown)
        {
            return thrown;
        }
    }

    [Fact]
    public void Driver_failures_are_wrapped_with_the_original_preserved()
    {
        var driver = new DriverException("boom");
        var provider = Assert.IsType<VelQueryProviderException>(TranslateCaught(driver, CancellationToken.None));
        Assert.Same(driver, provider.InnerException);
        Assert.Equal("q.id", provider.QueryId);

        var timeout = Assert.IsType<VelQueryTimeoutException>(TranslateCaught(new DriverTimeout(), CancellationToken.None));
        Assert.IsType<DriverTimeout>(timeout.InnerException);
    }

    [Fact]
    public void Velquery_errors_caller_exceptions_and_cancellation_pass_through()
    {
        var mapping = new VelQueryNoRowsException("q.id");
        Assert.Same(mapping, TranslateCaught(mapping, CancellationToken.None));

        var callerBug = new InvalidOperationException("caller code");
        Assert.Same(callerBug, TranslateCaught(callerBug, CancellationToken.None));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = Assert.IsType<OperationCanceledException>(TranslateCaught(new DriverException("aborted"), cts.Token));
        Assert.IsType<DriverException>(cancelled.InnerException);
        Assert.Equal(cts.Token, cancelled.CancellationToken);
    }

    [Fact]
    public async Task TryDisposeAsync_reports_failures_without_throwing()
    {
        Assert.Null(await AsyncDisposal.TryDisposeAsync(null));
        var resource = new Resource();
        Assert.Null(await AsyncDisposal.TryDisposeAsync(resource));
        Assert.Equal(1, resource.Disposed);
        Assert.IsType<DriverException>(await AsyncDisposal.TryDisposeAsync(new Resource { Fail = true }));
    }

    [Fact]
    public void Session_guard_rejects_overlapping_operations()
    {
        var guard = new SessionGuard();
        guard.Enter();
        Assert.Throws<VelQueryValidationException>(guard.Enter);
        guard.Exit();
        guard.Enter();
        guard.Exit();
    }

    private sealed class Resource : IAsyncDisposable
    {
        public int Disposed { get; private set; }

        public bool Fail { get; init; }

        public ValueTask DisposeAsync()
        {
            Disposed++;
            return Fail ? ValueTask.FromException(new DriverException("dispose failed")) : ValueTask.CompletedTask;
        }
    }

    /// <summary>A fake cursor-based engine stream.</summary>
    private sealed class FakeCursor(int items, Exception? openFault = null, int failAt = -1, bool releaseFails = false, CancellationToken token = default)
        : ResourceOwningAsyncEnumerator<int>("fake.stream", s_classifier, token)
    {
        private int _next;

        public int Opened { get; private set; }

        public int Released { get; private set; }

        public bool? ReleasedCompleted { get; private set; }

        protected override ValueTask OpenAsync(CancellationToken cancellationToken)
        {
            Opened++;
            return openFault is null ? ValueTask.CompletedTask : ValueTask.FromException(openFault);
        }

        protected override ValueTask<(bool HasItem, int Item)> ReadNextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_next == failAt)
            {
                throw new DriverException("read failed");
            }

            return ValueTask.FromResult(_next < items ? (true, ++_next) : (false, 0));
        }

        protected override ValueTask ReleaseAsync(bool completed)
        {
            Released++;
            ReleasedCompleted = completed;
            return releaseFails ? ValueTask.FromException(new DriverException("release failed")) : ValueTask.CompletedTask;
        }
    }

    private static async Task<List<int>> DrainAsync(IAsyncEnumerator<int> enumerator, int stopAfter = int.MaxValue)
    {
        var seen = new List<int>();
        await using (enumerator)
        {
            while (seen.Count < stopAfter && await enumerator.MoveNextAsync())
            {
                seen.Add(enumerator.Current);
            }
        }

        return seen;
    }

    [Fact]
    public async Task Stream_completes_and_releases_once_as_completed()
    {
        var cursor = new FakeCursor(3);
        Assert.Equal([1, 2, 3], await DrainAsync(cursor));
        Assert.False(await cursor.MoveNextAsync());
        Assert.Equal((1, 1, true), (cursor.Opened, cursor.Released, cursor.ReleasedCompleted!.Value));
    }

    [Fact]
    public async Task Early_exit_releases_once_as_not_completed()
    {
        var cursor = new FakeCursor(100);
        Assert.Equal([1, 2], await DrainAsync(cursor, stopAfter: 2));
        Assert.Equal((1, false), (cursor.Released, cursor.ReleasedCompleted!.Value));
    }

    [Fact]
    public async Task Disposing_before_enumeration_releases_nothing()
    {
        var cursor = new FakeCursor(3);
        await cursor.DisposeAsync();
        Assert.Equal((0, 0), (cursor.Opened, cursor.Released));
    }

    [Fact]
    public async Task Read_failures_release_and_follow_the_error_contract()
    {
        var cursor = new FakeCursor(10, failAt: 4);
        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => DrainAsync(cursor));
        Assert.Equal("fake.stream", ex.QueryId);
        Assert.Equal((1, false), (cursor.Released, cursor.ReleasedCompleted!.Value));
    }

    [Fact]
    public async Task Open_failures_release_once()
    {
        var cursor = new FakeCursor(10, openFault: new DriverTimeout());
        await Assert.ThrowsAsync<VelQueryTimeoutException>(() => DrainAsync(cursor));
        Assert.Equal(1, cursor.Released);
    }

    [Fact]
    public async Task Release_failure_never_hides_the_original_failure_but_surfaces_after_success()
    {
        var failing = new FakeCursor(10, failAt: 2, releaseFails: true);
        var original = await Assert.ThrowsAsync<VelQueryProviderException>(() => DrainAsync(failing));
        Assert.Equal("read failed", original.InnerException!.Message);

        var clean = new FakeCursor(2, releaseFails: true);
        var surfaced = await Assert.ThrowsAsync<VelQueryProviderException>(() => DrainAsync(clean));
        Assert.Equal("release failed", surfaced.InnerException!.Message);
        Assert.Equal(1, clean.Released);
    }

    [Fact]
    public async Task Consumer_exceptions_pass_through_and_release()
    {
        var cursor = new FakeCursor(10);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using (cursor)
            {
                while (await cursor.MoveNextAsync())
                {
                    throw new InvalidOperationException("consumer");
                }
            }
        });
        Assert.Equal((1, false), (cursor.Released, cursor.ReleasedCompleted!.Value));
    }

    [Fact]
    public async Task Cancellation_becomes_operation_canceled_and_releases()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var cursor = new FakeCursor(10, token: cts.Token);
        Assert.True(await cursor.MoveNextAsync());
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cursor.MoveNextAsync());
        Assert.Equal(1, cursor.Released);
        await cursor.DisposeAsync();
        Assert.Equal(1, cursor.Released);
    }
}
