using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace VelQuery.Sqlite.Bulk;

/// <summary>
/// High-throughput inserts for SQLite: one prepared <c>INSERT</c> re-executed per row inside one transaction, instead of
/// one autocommit (and one journal sync) per row. Runs on the session's connection with VelQuery's normal ownership,
/// cancellation and error contract (failures surface as <see cref="VelQueryProviderException"/> /
/// <see cref="VelQueryTimeoutException"/>).
/// </summary>
/// <remarks>
/// All-or-nothing (VQ-DB-004): without a session transaction the load commits its own transaction only after every
/// row is written; inside a session transaction it runs in a savepoint that is rolled back on failure, so the session
/// transaction is left exactly as it was. Cancellation is checked before every row.
/// </remarks>
public static class VelQuerySqliteBulkExtensions
{
    private const string Savepoint = "velquery_bulk";

    /// <summary>Writes <paramref name="rows"/> and returns the number of rows SQLite reports as inserted.</summary>
    public static Task<long> BulkInsertAsync<T>(this IVelQuerySession session, SqliteBulkInsertPlan<T> plan, IEnumerable<T> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rows);
        return session.ExecuteOnConnectionAsync(plan.QueryId, (plan, rows), static (context, state, ct) =>
            RunAsync(context, state.plan, (insert, token) =>
            {
                long written = 0;
                foreach (var row in state.rows)
                {
                    token.ThrowIfCancellationRequested();
                    written += insert.Execute(row);
                }

                return Task.FromResult(written);
            }, ct), cancellationToken);
    }

    /// <summary>Writes rows produced asynchronously; rows are pulled one at a time, so memory stays bounded.</summary>
    public static Task<long> BulkInsertAsync<T>(this IVelQuerySession session, SqliteBulkInsertPlan<T> plan, IAsyncEnumerable<T> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rows);
        return session.ExecuteOnConnectionAsync(plan.QueryId, (plan, rows), static (context, state, ct) =>
            RunAsync(context, state.plan, async (insert, token) =>
            {
                long written = 0;
                await foreach (var row in state.rows.WithCancellation(token).ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested();
                    written += insert.Execute(row);
                }

                return written;
            }, ct), cancellationToken);
    }

    private static async Task<long> RunAsync<T>(ConnectionContext context, SqliteBulkInsertPlan<T> plan,
        Func<PreparedInsert<T>, CancellationToken, Task<long>> write, CancellationToken cancellationToken)
    {
        if (plan.Columns.IsEmpty)
        {
            throw new VelQueryValidationException(plan.QueryId, "the bulk insert plan maps no columns.");
        }

        if (context.Connection is not SqliteConnection connection || (context.Transaction is not null and not SqliteTransaction))
        {
            throw new VelQueryUnsupportedCapabilityException(plan.QueryId, "bulk insert requires a Microsoft.Data.Sqlite connection.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sessionTransaction = (SqliteTransaction?)context.Transaction;
        // VelQuery owns this transaction (it is never the caller's); it commits only after every row is written.
        // Microsoft.Data.Sqlite begins it with BEGIN IMMEDIATE, so the write lock is taken up front.
        SqliteTransaction? ownTransaction = null;
        var savepointOpen = false;
        try
        {
            if (sessionTransaction is null)
            {
                ownTransaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await sessionTransaction.SaveAsync(Savepoint, cancellationToken).ConfigureAwait(false);
                savepointOpen = true;
            }

            // One command for this call only: commands are never cached across calls (AGENTS rule 7).
            using (var insert = new PreparedInsert<T>(connection, ownTransaction ?? sessionTransaction!, plan, TimeoutSeconds(plan, context)))
            {
                var written = await write(insert, cancellationToken).ConfigureAwait(false);
                if (ownTransaction is not null)
                {
                    await ownTransaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await sessionTransaction!.ReleaseAsync(Savepoint, CancellationToken.None).ConfigureAwait(false);
                    savepointOpen = false;
                }

                return written;
            }
        }
        catch when (savepointOpen)
        {
            // Undo this load only; the session transaction stays active and usable. A failed rollback must not hide
            // the original failure (AGENTS rule 1).
            try
            {
                await sessionTransaction!.RollbackAsync(Savepoint, CancellationToken.None).ConfigureAwait(false);
                await sessionTransaction.ReleaseAsync(Savepoint, CancellationToken.None).ConfigureAwait(false);
            }
            catch (DbException)
            {
            }

            throw;
        }
        finally
        {
            if (ownTransaction is not null)
            {
                // Rolls back if the commit above was not reached.
                await ownTransaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static int? TimeoutSeconds<T>(SqliteBulkInsertPlan<T> plan, ConnectionContext context)
    {
        if (plan.Options.Timeout is not { } timeout)
        {
            return context.CommandTimeoutSeconds;
        }

        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return 0;
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new VelQueryValidationException(plan.QueryId, "the bulk insert timeout must be positive or infinite.");
        }

        return (int)Math.Min(int.MaxValue, Math.Ceiling(timeout.TotalSeconds));
    }

    /// <summary>The per-call prepared statement and its parameters, re-assigned for every row.</summary>
    private sealed class PreparedInsert<T> : IDisposable
    {
        private readonly SqliteCommand _command;
        private readonly SqliteParameter[] _parameters;
        private readonly Func<T, object?>[] _getters;

        public PreparedInsert(SqliteConnection connection, SqliteTransaction transaction, SqliteBulkInsertPlan<T> plan, int? timeoutSeconds)
        {
            _command = connection.CreateCommand();
            try
            {
                // Microsoft.Data.Sqlite rejects a command without the connection's pending transaction.
                _command.Transaction = transaction;
                _command.CommandText = plan.InsertCommand;
                if (timeoutSeconds is { } seconds)
                {
                    _command.CommandTimeout = seconds;
                }

                var columns = plan.Columns;
                _parameters = new SqliteParameter[columns.Length];
                _getters = new Func<T, object?>[columns.Length];
                for (var i = 0; i < columns.Length; i++)
                {
                    var parameter = new SqliteParameter { ParameterName = "@p" + i, Value = DBNull.Value };
                    if (columns[i].Type is { } type)
                    {
                        parameter.SqliteType = type;
                    }

                    _parameters[i] = parameter;
                    _getters[i] = columns[i].Getter;
                    _command.Parameters.Add(parameter);
                }

                _command.Prepare();
            }
            catch
            {
                _command.Dispose();
                throw;
            }
        }

        public int Execute(T row)
        {
            for (var i = 0; i < _parameters.Length; i++)
            {
                _parameters[i].Value = _getters[i](row) ?? DBNull.Value;
            }

            // Synchronous on purpose: Microsoft.Data.Sqlite's async methods complete synchronously, and the async
            // wrapper adds a task and a cancellation check per row. Cancellation is checked by the caller per row.
            return _command.ExecuteNonQuery();
        }

        public void Dispose() => _command.Dispose();
    }
}
