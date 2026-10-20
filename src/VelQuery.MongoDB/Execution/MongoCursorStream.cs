using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;
using VelQuery.Engine;

namespace VelQuery.MongoDB.Execution;

/// <summary>
/// A lazily executed cursor stream. Each enumeration runs the operation once; the enumerator owns the cursor, the
/// session guard and any linked token source until it completes or is disposed (VQ-FR-013, ADR-0016).
/// </summary>
internal sealed class MongoCursorStream<T>(VelQueryMongoSession session, string queryId, VelQueryMongoSession.CursorOpener<T> opener, CancellationToken cancellationToken)
    : IAsyncEnumerable<T>
{
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken enumeratorToken = default)
    {
        // Honor both the method's token and the one passed to GetAsyncEnumerator (WithCancellation).
        CancellationTokenSource? linked = null;
        var token = cancellationToken;
        if (enumeratorToken.CanBeCanceled)
        {
            if (cancellationToken.CanBeCanceled && cancellationToken != enumeratorToken)
            {
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, enumeratorToken);
                token = linked.Token;
            }
            else
            {
                token = enumeratorToken;
            }
        }

        var enumerator = new Enumerator(session, queryId, opener, token);
        return linked is null ? enumerator : new LinkedEnumerator(enumerator, linked);
    }

    /// <summary>Disposes the linked token source even when enumeration never started.</summary>
    private sealed class LinkedEnumerator(Enumerator inner, CancellationTokenSource linked) : IAsyncEnumerator<T>
    {
        public T Current => inner.Current;

        public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();

        public async ValueTask DisposeAsync()
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                linked.Dispose();
            }
        }
    }

    private sealed class Enumerator(VelQueryMongoSession session, string queryId, VelQueryMongoSession.CursorOpener<T> opener, CancellationToken token)
        : ResourceOwningAsyncEnumerator<T>(queryId, MongoErrorClassifier.Instance, token)
    {
        private VelQueryMongoSession.StreamLease? _lease;
        private IAsyncCursor<T>? _cursor;
        private IEnumerator<T>? _batch;

        protected override async ValueTask OpenAsync(CancellationToken cancellationToken)
        {
            var lease = session.BeginStream(opener.Descriptor, cancellationToken);
            _lease = lease;
            _cursor = await opener.OpenAsync(lease.Session, lease.MaxTime, cancellationToken).ConfigureAwait(false);
        }

        protected override async ValueTask<(bool HasItem, T Item)> ReadNextAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                if (_batch is not null && _batch.MoveNext())
                {
                    return (true, _batch.Current);
                }

                _batch?.Dispose();
                _batch = null;
                if (!await _cursor!.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                {
                    return (false, default!);
                }

                _batch = _cursor.Current.GetEnumerator();
            }
        }

        protected override ValueTask ReleaseAsync(bool completed)
        {
            // Each step runs even if an earlier one fails, so the guard is always released. AsyncCursor.Dispose sends
            // killCursors for an unexhausted server cursor (early exit) and swallows its own failures.
            try
            {
                _batch?.Dispose();
                _cursor?.Dispose();
            }
            finally
            {
                _batch = null;
                _cursor = null;
                _lease?.Release();
                _lease = null;
            }

            return ValueTask.CompletedTask;
        }
    }
}
