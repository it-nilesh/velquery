using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace VelQuery.MySql.Bulk;

/// <summary>
/// High-throughput inserts for MySQL and MariaDB via <c>LOAD DATA LOCAL INFILE</c> (<see cref="MySqlBulkCopy"/>): one
/// streamed statement instead of one round trip per row. Runs on the session's connection and transaction with VelQuery's
/// normal ownership, cancellation and error contract.
/// </summary>
/// <remarks>
/// <para>Requires <c>AllowLoadLocalInfile=true</c> in the connection string and <c>local_infile=ON</c> on the server;
/// otherwise the load fails with <see cref="VelQueryUnsupportedCapabilityException"/>.</para>
/// <para>A LOCAL load has <c>IGNORE</c> semantics on the server even in strict mode: rows violating a unique key, CHECK or
/// foreign key are skipped, and NULL into NOT NULL or over-long values are coerced with a warning. VelQuery never lets
/// that pass silently: the load runs in its own transaction (or under a savepoint inside the session transaction), and if
/// any row was skipped or any warning raised, the load is rolled back and a <see cref="VelQueryProviderException"/> wrapping
/// <see cref="MySqlBulkInsertException"/> (or MySqlConnector's <c>BulkCopyFailed</c> error) is thrown.</para>
/// </remarks>
public static class VelQueryMySqlBulkExtensions
{
    private const string Savepoint = "velquery_bulk";

    // ER_CLIENT_LOCAL_FILES_DISABLED (MySQL 8), ER_LOCAL_INFILE_DISABLED-style rejections (MariaDB 10.x/11.x).
    private const int MySqlLocalInfileDisabled = 3948;
    private const int MariaDbLocalInfileDisabled = 4166;
    private const int NotAllowedCommand = 1148;

    private const int MaxWarnings = 64;

    /// <summary>Writes <paramref name="rows"/> and returns the number of rows inserted.</summary>
    public static Task<long> BulkInsertAsync<T>(this IVelQuerySession session, MySqlBulkInsertPlan<T> plan, IEnumerable<T> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rows);
        return session.ExecuteOnConnectionAsync(plan.QueryId, (plan, rows), static (context, state, ct) =>
            LoadAsync(context, state.plan, () => new BulkRowReader<T>(state.plan, state.rows), ct), cancellationToken);
    }

    /// <summary>
    /// Writes rows produced asynchronously (for example a channel or another query's stream) and returns the number of
    /// rows inserted. Rows are pulled as the server accepts them, so memory stays bounded.
    /// </summary>
    public static Task<long> BulkInsertAsync<T>(this IVelQuerySession session, MySqlBulkInsertPlan<T> plan, IAsyncEnumerable<T> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rows);
        return session.ExecuteOnConnectionAsync(plan.QueryId, (plan, rows), static (context, state, ct) =>
            LoadAsync(context, state.plan, () => new BulkRowReader<T>(state.plan, state.rows, ct), ct), cancellationToken);
    }

    private static async Task<long> LoadAsync<T>(ConnectionContext context, MySqlBulkInsertPlan<T> plan, Func<BulkRowReader<T>> createReader, CancellationToken cancellationToken)
    {
        if (plan.Columns.IsEmpty)
        {
            throw new VelQueryValidationException(plan.QueryId, "the bulk insert plan maps no columns.");
        }

        if (context.Connection is not MySqlConnection connection || (context.Transaction is not null and not MySqlTransaction))
        {
            throw new VelQueryUnsupportedCapabilityException(plan.QueryId, "bulk insert requires a MySqlConnector connection.");
        }

        var settings = new MySqlConnectionStringBuilder(connection.ConnectionString);
        if (!settings.AllowLoadLocalInfile)
        {
            throw LocalInfileDisabled(plan.QueryId, "the connection string does not set AllowLoadLocalInfile=true");
        }

        var timeout = BulkTimeoutSeconds(plan, context);
        var sessionTransaction = (MySqlTransaction?)context.Transaction;
        if (plan.HasHexEncodedColumns)
        {
            await ValidateHexEncodedColumnsAsync(connection, sessionTransaction, plan, settings, cancellationToken).ConfigureAwait(false);
        }

        // VelQuery owns this transaction (it is never the caller's); it commits only after the load is verified. Inside a
        // session transaction a savepoint gives the same statement-level all-or-nothing without ending that transaction.
        MySqlTransaction? ownTransaction = null;
        if (sessionTransaction is null)
        {
            ownTransaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await sessionTransaction.SaveAsync(Savepoint, cancellationToken).ConfigureAwait(false);
        }

        var transaction = ownTransaction ?? sessionTransaction!;
        var completed = false;
        try
        {
            var reader = createReader();
            await using (reader.ConfigureAwait(false))
            {
                var bulk = new MySqlBulkCopy(connection, transaction) { DestinationTableName = plan.QuotedTable };
                if (timeout is { } seconds)
                {
                    bulk.BulkCopyTimeout = seconds;
                }

                for (var i = 0; i < plan.Columns.Length; i++)
                {
                    // MySqlBulkCopy quotes destination column names itself; names are validated by the plan.
                    bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(i, plan.Columns[i].Name));
                }

                MySqlBulkCopyResult result;
                try
                {
                    result = await bulk.WriteToServerAsync(reader, cancellationToken).ConfigureAwait(false);
                }
                catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.BulkCopyFailed && !cancellationToken.IsCancellationRequested)
                {
                    // Rows were skipped (duplicate key, CHECK, foreign key). Read the reasons before rolling back clears them.
                    var warnings = await ReadWarningsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    throw new MySqlBulkInsertException(reader.RowsRead, null, warnings, ex);
                }
                catch (MySqlException ex) when (ex.Number is MySqlLocalInfileDisabled or MariaDbLocalInfileDisabled or NotAllowedCommand)
                {
                    throw LocalInfileDisabled(plan.QueryId, "the server rejected LOAD DATA LOCAL (set local_infile=ON)");
                }
                catch (Exception) when (reader.SourceFailure is { } sourceFailure)
                {
                    // The caller's rows or getters failed (including cancellation observed by the source); report that
                    // failure itself rather than MySqlBulkCopy's "Error during LOAD DATA LOCAL INFILE" wrapper.
                    sourceFailure.Throw();
                    throw;
                }

                if (result.RowsInserted != reader.RowsRead || HasWarnings(result.Warnings))
                {
                    throw new MySqlBulkInsertException(reader.RowsRead, result.RowsInserted, ToWarnings(result.Warnings));
                }

                if (ownTransaction is not null)
                {
                    await ownTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await sessionTransaction!.ReleaseAsync(Savepoint, cancellationToken).ConfigureAwait(false);
                }

                completed = true;
                return result.RowsInserted;
            }
        }
        finally
        {
            if (ownTransaction is not null)
            {
                // Rolls back if the commit above was not reached.
                await DisposeQuietlyAsync(ownTransaction, completed).ConfigureAwait(false);
            }
            else if (!completed)
            {
                await RollbackToSavepointQuietlyAsync(sessionTransaction!).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// MySqlBulkCopy sends <see cref="Guid"/> (under a binary <c>GuidFormat</c>) and <see cref="byte"/>[] values as hex and
    /// wraps them in <c>UNHEX()</c> only when the destination column is binary. A mismatched destination is coerced without
    /// an error (MariaDB stores NULL for a text GUID into BINARY(16) without even a warning), so the destination types of
    /// those columns are checked up front with a zero-row query.
    /// </summary>
    private static async Task ValidateHexEncodedColumnsAsync<T>(MySqlConnection connection, MySqlTransaction? transaction, MySqlBulkInsertPlan<T> plan,
        MySqlConnectionStringBuilder settings, CancellationToken cancellationToken)
    {
        var textGuids = settings.GuidFormat is MySqlGuidFormat.Char36 or MySqlGuidFormat.Char32 || (settings.GuidFormat == MySqlGuidFormat.Default && !settings.OldGuids);
        var sql = new System.Text.StringBuilder("SELECT ");
        for (var i = 0; i < plan.Columns.Length; i++)
        {
            sql.Append(i == 0 ? string.Empty : ", ").Append(MySqlProvider.Instance.QuoteIdentifier(plan.Columns[i].Name));
        }

        sql.Append(" FROM ").Append(plan.QuotedTable).Append(" LIMIT 0");
        var command = new MySqlCommand(sql.ToString(), connection, transaction);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SchemaOnly, cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var schema = await reader.GetColumnSchemaAsync(cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < plan.Columns.Length; i++)
                {
                    var column = plan.Columns[i];
                    var destination = schema[i] is MySqlDbColumn { ProviderType: var type } ? type : (MySqlDbType?)null;
                    if (destination is null || destination == MySqlDbType.Guid)
                    {
                        continue;
                    }

                    var binary = destination is MySqlDbType.Binary or MySqlDbType.VarBinary or MySqlDbType.TinyBlob or MySqlDbType.Blob or MySqlDbType.MediumBlob or MySqlDbType.LongBlob;
                    if (column.ValueType == typeof(Guid) && (binary || !textGuids))
                    {
                        throw new VelQueryValidationException(plan.QueryId,
                            $"column '{column.Name}' is {schema[i].DataTypeName} but the connection's GuidFormat ({settings.GuidFormat}) does not map it to Guid. " +
                            "Set GuidFormat (for example Binary16 or TimeSwapBinary16 for BINARY(16), Char36 for CHAR(36)) or map the value explicitly.");
                    }

                    if (column.ValueType == typeof(byte[]) && !binary)
                    {
                        throw new VelQueryValidationException(plan.QueryId,
                            $"column '{column.Name}' is {schema[i].DataTypeName}; byte[] values can only be bulk loaded into BINARY, VARBINARY or BLOB columns.");
                    }
                }
            }
        }
    }

    private static int? BulkTimeoutSeconds<T>(MySqlBulkInsertPlan<T> plan, ConnectionContext context)
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
            throw new VelQueryValidationException(plan.QueryId, "the bulk insert timeout must be positive or Timeout.InfiniteTimeSpan.");
        }

        return (int)Math.Min(int.MaxValue, Math.Ceiling(timeout.TotalSeconds));
    }

    private static bool HasWarnings(IReadOnlyList<MySqlError> warnings)
    {
        foreach (var warning in warnings)
        {
            // Notes are informational (for example deprecation notices); warnings and errors mean data was altered.
            if (!string.Equals(warning.Level, "Note", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<MySqlBulkWarning> ToWarnings(IReadOnlyList<MySqlError> warnings)
    {
        var result = new List<MySqlBulkWarning>(Math.Min(warnings.Count, MaxWarnings));
        for (var i = 0; i < warnings.Count && i < MaxWarnings; i++)
        {
            result.Add(new MySqlBulkWarning(warnings[i].Level, (int)warnings[i].ErrorCode, warnings[i].Message));
        }

        return result;
    }

    private static async Task<List<MySqlBulkWarning>> ReadWarningsAsync(MySqlConnection connection, MySqlTransaction transaction, CancellationToken cancellationToken)
    {
        var warnings = new List<MySqlBulkWarning>();
        try
        {
            var command = new MySqlCommand("SHOW WARNINGS LIMIT " + MaxWarnings.ToString(CultureInfo.InvariantCulture), connection, transaction);
            await using (command.ConfigureAwait(false))
            {
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        warnings.Add(new MySqlBulkWarning(reader.GetString(0), Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture), reader.GetString(2)));
                    }
                }
            }
        }
        catch (DbException)
        {
            // Diagnostics are best effort; the original failure is still reported.
        }

        return warnings;
    }

    private static async ValueTask DisposeQuietlyAsync(MySqlTransaction transaction, bool completed)
    {
        try
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception) when (!completed)
        {
            // Cleanup failures must not hide the original failure (the connection is discarded by the pool if broken).
        }
    }

    private static async ValueTask RollbackToSavepointQuietlyAsync(MySqlTransaction transaction)
    {
        try
        {
            // Undo only this load; the session transaction stays open and remains the caller's to complete.
            await transaction.RollbackAsync(Savepoint, CancellationToken.None).ConfigureAwait(false);
            await transaction.ReleaseAsync(Savepoint, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // Cleanup failures must not hide the original failure.
        }
    }

    private static VelQueryUnsupportedCapabilityException LocalInfileDisabled(string queryId, string reason)
        => new(queryId, $"MySQL bulk insert uses LOAD DATA LOCAL INFILE, but {reason}. Enable AllowLoadLocalInfile=true on the client and local_infile=ON on the server, or insert with ExecuteAsync.");
}
