using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace VelQuery.PostgreSQL.Bulk;

/// <summary>
/// High-throughput inserts for PostgreSQL via binary <c>COPY</c>: one streamed statement instead of one round trip per
/// row. Runs on the session's connection (and therefore inside its transaction, if any) with VelQuery's normal
/// ownership, cancellation and error contract.
/// </summary>
public static class VelQueryPostgreSqlBulkExtensions
{
    /// <summary>Writes <paramref name="rows"/> and returns the number of rows written.</summary>
    public static Task<long> BulkInsertAsync<T>(this IVelQuerySession session, PostgreSqlBulkInsertPlan<T> plan, IEnumerable<T> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rows);
        return session.ExecuteOnConnectionAsync(plan.QueryId, (plan, rows), static async (context, state, ct) =>
        {
            var importer = await BeginAsync(context, state.plan, ct).ConfigureAwait(false);
            await using (importer.ConfigureAwait(false))
            {
                long written = 0;
                foreach (var row in state.rows)
                {
                    await WriteRowAsync(importer, state.plan, row, ct).ConfigureAwait(false);
                    written++;
                }

                await importer.CompleteAsync(ct).ConfigureAwait(false);
                return written;
            }
        }, cancellationToken);
    }

    /// <summary>Writes rows produced asynchronously; rows are pulled as the server accepts them, so memory stays bounded.</summary>
    public static Task<long> BulkInsertAsync<T>(this IVelQuerySession session, PostgreSqlBulkInsertPlan<T> plan, IAsyncEnumerable<T> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rows);
        return session.ExecuteOnConnectionAsync(plan.QueryId, (plan, rows), static async (context, state, ct) =>
        {
            var importer = await BeginAsync(context, state.plan, ct).ConfigureAwait(false);
            await using (importer.ConfigureAwait(false))
            {
                long written = 0;
                await foreach (var row in state.rows.WithCancellation(ct).ConfigureAwait(false))
                {
                    await WriteRowAsync(importer, state.plan, row, ct).ConfigureAwait(false);
                    written++;
                }

                await importer.CompleteAsync(ct).ConfigureAwait(false);
                return written;
            }
        }, cancellationToken);
    }

    private static async Task<NpgsqlBinaryImporter> BeginAsync<T>(ConnectionContext context, PostgreSqlBulkInsertPlan<T> plan, CancellationToken cancellationToken)
    {
        if (plan.Columns.IsEmpty)
        {
            throw new VelQueryValidationException(plan.QueryId, "the bulk insert plan maps no columns.");
        }

        if (context.Connection is not NpgsqlConnection connection)
        {
            throw new VelQueryUnsupportedCapabilityException(plan.QueryId, "bulk insert requires an Npgsql connection.");
        }

        // Disposing the importer without CompleteAsync cancels the COPY, so a failure persists nothing.
        var importer = await connection.BeginBinaryImportAsync(plan.CopyCommand, cancellationToken).ConfigureAwait(false);
        if (plan.Options.Timeout is { } timeout)
        {
            importer.Timeout = timeout;
        }
        else if (context.CommandTimeoutSeconds is { } seconds)
        {
            importer.Timeout = seconds == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
        }

        return importer;
    }

    private static async ValueTask WriteRowAsync<T>(NpgsqlBinaryImporter importer, PostgreSqlBulkInsertPlan<T> plan, T row, CancellationToken cancellationToken)
    {
        await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
        foreach (var column in plan.Columns)
        {
            // Writes usually complete synchronously into Npgsql's buffer; only await when a flush is pending.
            var write = column.Write(importer, row, cancellationToken);
            if (!write.IsCompletedSuccessfully)
            {
                await write.ConfigureAwait(false);
            }
        }
    }
}
