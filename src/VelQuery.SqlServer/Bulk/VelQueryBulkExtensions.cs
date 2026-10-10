using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace VelQuery.SqlServer.Bulk;

/// <summary>
/// High-throughput inserts for SQL Server via <see cref="SqlBulkCopy"/>: one streamed operation instead of one round
/// trip and one commit per row. Runs on the session's connection and transaction with VelQuery's normal ownership,
/// cancellation and error contract (failures surface as <see cref="VelQueryProviderException"/> /
/// <see cref="VelQueryTimeoutException"/>).
/// </summary>
public static class VelQueryBulkExtensions
{
    /// <summary>Writes <paramref name="rows"/> and returns the number of rows written.</summary>
    public static Task<long> BulkInsertAsync<T>(this IVelQuerySession session, SqlBulkInsertPlan<T> plan, IEnumerable<T> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rows);
        return session.ExecuteOnConnectionAsync(plan.QueryId, (plan, rows), static (context, state, ct) =>
            CopyAsync(context, state.plan, (sink, token) => sink.WriteAllAsync(state.rows, token), ct), cancellationToken);
    }

    /// <summary>
    /// Writes rows produced asynchronously (for example a channel or another query's stream) and returns the number of
    /// rows written. Rows are pulled as the server accepts them, so memory stays bounded.
    /// </summary>
    public static Task<long> BulkInsertAsync<T>(this IVelQuerySession session, SqlBulkInsertPlan<T> plan, IAsyncEnumerable<T> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rows);
        return session.ExecuteOnConnectionAsync(plan.QueryId, (plan, rows), static (context, state, ct) =>
            CopyAsync(context, state.plan, (sink, token) => sink.WriteAllAsync(state.rows, token), ct), cancellationToken);
    }

    private static async Task<long> CopyAsync<T>(ConnectionContext context, SqlBulkInsertPlan<T> plan,
        Func<BulkSink<T>, CancellationToken, Task<long>> write, CancellationToken cancellationToken)
    {
        if (plan.Columns.IsEmpty)
        {
            throw new VelQueryValidationException(plan.QueryId, "the bulk insert plan maps no columns.");
        }

        if (context.Connection is not SqlConnection connection || (context.Transaction is not null and not SqlTransaction))
        {
            throw new VelQueryUnsupportedCapabilityException(plan.QueryId, "bulk insert requires a Microsoft.Data.SqlClient connection.");
        }

        var options = plan.Options;
        // VelQuery owns this transaction (it is never the caller's); it commits only after every row is written.
        SqlTransaction? ownTransaction = null;
        var transaction = (SqlTransaction?)context.Transaction;
        if (transaction is null && options.Atomic)
        {
            ownTransaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            transaction = ownTransaction;
        }

        try
        {
            using var bulk = new SqlBulkCopy(connection, ToSqlOptions(options), transaction)
            {
                DestinationTableName = plan.QuotedTable,
                // Each staged chunk is written as one batch.
                BatchSize = 0,
                BulkCopyTimeout = options.Timeout is { } t ? (t == Timeout.InfiniteTimeSpan ? 0 : (int)Math.Ceiling(t.TotalSeconds)) : context.CommandTimeoutSeconds ?? 30,
            };
            foreach (var column in plan.Columns)
            {
                bulk.ColumnMappings.Add(column.Name, column.Name);
            }

            using var sink = new BulkSink<T>(plan, bulk);
            var written = await write(sink, cancellationToken).ConfigureAwait(false);
            if (ownTransaction is not null)
            {
                await ownTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return written;
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

    private static SqlBulkCopyOptions ToSqlOptions(SqlBulkInsertOptions options)
    {
        var result = SqlBulkCopyOptions.Default;
        if (options.CheckConstraints)
        {
            result |= SqlBulkCopyOptions.CheckConstraints;
        }

        if (options.FireTriggers)
        {
            result |= SqlBulkCopyOptions.FireTriggers;
        }

        if (options.KeepNulls)
        {
            result |= SqlBulkCopyOptions.KeepNulls;
        }

        if (options.TableLock)
        {
            result |= SqlBulkCopyOptions.TableLock;
        }

        return result;
    }
}
