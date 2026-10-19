using System;
using System.Collections.Generic;
using System.Data.Common;

namespace VelQuery.MySql.Bulk;

/// <summary>
/// Raised (as the inner exception of <see cref="VelQueryProviderException"/>) when MySQL accepted a <c>LOAD DATA</c> but
/// skipped rows or coerced values, which plain <c>INSERT</c> in strict mode would have rejected. The load has been rolled
/// back. The message carries only counts and warning codes; <see cref="Warnings"/> holds the server's text, which can
/// echo row values.
/// </summary>
public sealed class MySqlBulkInsertException : DbException
{
    internal MySqlBulkInsertException(long rowsSent, long? rowsInserted, IReadOnlyList<MySqlBulkWarning> warnings, Exception? innerException = null)
        : base(Describe(rowsSent, rowsInserted, warnings), innerException)
    {
        RowsSent = rowsSent;
        RowsInserted = rowsInserted;
        Warnings = warnings;
    }

    /// <summary>Rows read from the source and sent to the server.</summary>
    public long RowsSent { get; }

    /// <summary>Rows the server reported as inserted, when known.</summary>
    public long? RowsInserted { get; }

    /// <summary>The server's warnings for the load (at most 64).</summary>
    public IReadOnlyList<MySqlBulkWarning> Warnings { get; }

    private static string Describe(long rowsSent, long? rowsInserted, IReadOnlyList<MySqlBulkWarning> warnings)
    {
        var codes = new SortedSet<int>();
        foreach (var warning in warnings)
        {
            codes.Add(warning.Code);
        }

        var inserted = rowsInserted is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "fewer";
        return $"LOAD DATA sent {rowsSent} row(s), the server inserted {inserted} and raised {warnings.Count} warning(s)"
            + (codes.Count > 0 ? $" (codes {string.Join(", ", codes)})" : string.Empty)
            + "; the load was rolled back. See Warnings for details.";
    }
}

/// <summary>A server diagnostic raised by a bulk load (<c>SHOW WARNINGS</c> row).</summary>
/// <param name="Level"><c>Note</c>, <c>Warning</c> or <c>Error</c>.</param>
/// <param name="Code">MySQL error/warning number, for example 1062 (duplicate key) or 1265 (data truncated).</param>
/// <param name="Message">Server text; may contain row values.</param>
public sealed record MySqlBulkWarning(string Level, int Code, string Message);
