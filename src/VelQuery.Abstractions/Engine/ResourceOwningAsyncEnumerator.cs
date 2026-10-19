using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VelQuery.Engine;

/// <summary>
/// Base for engine streams (VQ-FR-013, VQ-FR-027): the enumerator owns everything it opens until it completes or is
/// disposed, including on early exit, consumer exceptions and cancellation; resources are released exactly once; and
/// failures follow <see cref="VelQueryErrors"/>. Engines implement opening, reading the next item and releasing.
/// </summary>
/// <remarks>The ADO.NET engine keeps a specialized enumerator with a synchronous fast path; it follows the same contract.</remarks>
public abstract class ResourceOwningAsyncEnumerator<T> : IAsyncEnumerator<T>
{
    private readonly IVelQueryErrorClassifier _classifier;
    private bool _opened;
    private bool _finished;

    /// <summary>Creates the enumerator.</summary>
    protected ResourceOwningAsyncEnumerator(string queryId, IVelQueryErrorClassifier classifier, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryId);
        ArgumentNullException.ThrowIfNull(classifier);
        QueryId = queryId;
        _classifier = classifier;
        CancellationToken = cancellationToken;
    }

    /// <summary>The query identity used in errors.</summary>
    protected string QueryId { get; }

    /// <summary>The token governing the whole enumeration.</summary>
    protected CancellationToken CancellationToken { get; }

    /// <inheritdoc />
    public T Current { get; private set; } = default!;

    /// <summary>Acquires resources and starts the query (runs once, before the first item).</summary>
    protected abstract ValueTask OpenAsync(CancellationToken cancellationToken);

    /// <summary>Reads the next item; returns false at the end.</summary>
    protected abstract ValueTask<(bool HasItem, T Item)> ReadNextAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Releases everything acquired. <paramref name="completed"/> is false on early exit or failure, letting engines
    /// cancel server-side work instead of draining it. Called exactly once if <see cref="OpenAsync"/> was started.
    /// </summary>
    protected abstract ValueTask ReleaseAsync(bool completed);

    /// <inheritdoc />
    public async ValueTask<bool> MoveNextAsync()
    {
        if (_finished)
        {
            return false;
        }

        try
        {
            if (!_opened)
            {
                _opened = true;
                await OpenAsync(CancellationToken).ConfigureAwait(false);
            }

            var (hasItem, item) = await ReadNextAsync(CancellationToken).ConfigureAwait(false);
            if (hasItem)
            {
                Current = item;
                return true;
            }

            Current = default!;
            await FinishAsync(completed: true).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            await FinishAsync(completed: false, suppressFailures: true).ConfigureAwait(false);
            throw VelQueryErrors.Translate(ex, QueryId, _classifier, CancellationToken);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await FinishAsync(completed: false, suppressFailures: true).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async ValueTask FinishAsync(bool completed, bool suppressFailures = false)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        if (!_opened)
        {
            return;
        }

        try
        {
            await ReleaseAsync(completed).ConfigureAwait(false);
        }
        catch (Exception ex) when (suppressFailures)
        {
            // A release failure must not hide the failure (or early exit) that triggered it.
            _ = ex;
        }
        catch (Exception ex)
        {
            throw VelQueryErrors.Translate(ex, QueryId, _classifier, CancellationToken);
        }
    }
}
