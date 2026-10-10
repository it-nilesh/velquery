using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace VelQuery.SqlServer.Bulk;

/// <summary>
/// Stages rows into one reusable, typed <see cref="DataTable"/> of <c>BatchSize</c> rows and writes each chunk with
/// <see cref="SqlBulkCopy.WriteToServerAsync(DataTable, CancellationToken)"/>. Measured on SqlClient 7.1: streaming a
/// custom <see cref="System.Data.Common.DbDataReader"/> through the async API costs roughly 10x the client CPU (per-row
/// continuations), while chunked tables keep CPU at the driver's floor and memory bounded by one chunk.
/// </summary>
internal sealed class BulkSink<T> : IDisposable
{
    private readonly SqlBulkInsertPlan<T> _plan;
    private readonly SqlBulkCopy _bulk;
    private readonly DataTable _table = new() { Locale = System.Globalization.CultureInfo.InvariantCulture };
    private readonly object?[] _values;
    private readonly int _chunkSize;
    private long _written;

    public BulkSink(SqlBulkInsertPlan<T> plan, SqlBulkCopy bulk)
    {
        _plan = plan;
        _bulk = bulk;
        _chunkSize = plan.Options.BatchSize > 0 ? plan.Options.BatchSize : 10_000;
        _values = new object?[plan.Columns.Length];
        foreach (var column in plan.Columns)
        {
            _table.Columns.Add(new DataColumn(column.Name, StagingType(column.ValueType)) { AllowDBNull = true });
        }
    }

    public async Task<long> WriteAllAsync(IEnumerable<T> rows, CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            if (Stage(row))
            {
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await FlushAsync(cancellationToken).ConfigureAwait(false);
        return _written;
    }

    public async Task<long> WriteAllAsync(IAsyncEnumerable<T> rows, CancellationToken cancellationToken)
    {
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (Stage(row))
            {
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await FlushAsync(cancellationToken).ConfigureAwait(false);
        return _written;
    }

    /// <summary>Adds one row; returns true when the chunk is full.</summary>
    private bool Stage(T row)
    {
        var columns = _plan.Columns;
        for (var i = 0; i < columns.Length; i++)
        {
            _values[i] = columns[i].Getter(row) ?? DBNull.Value;
        }

        _table.Rows.Add(_values);
        return _table.Rows.Count >= _chunkSize;
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_table.Rows.Count == 0)
        {
            return;
        }

        await _bulk.WriteToServerAsync(_table, cancellationToken).ConfigureAwait(false);
        _written += _table.Rows.Count;
        _table.Clear();
    }

    public void Dispose() => _table.Dispose();

    /// <summary>
    /// Maps value types to staging column types with typeof literals (trimming-safe). Anything else is staged as
    /// <see cref="object"/> and converted by SqlClient against the destination column.
    /// </summary>
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    private static Type StagingType(Type type)
    {
        if (type == typeof(string)) { return typeof(string); }
        if (type == typeof(int)) { return typeof(int); }
        if (type == typeof(long)) { return typeof(long); }
        if (type == typeof(short)) { return typeof(short); }
        if (type == typeof(byte)) { return typeof(byte); }
        if (type == typeof(bool)) { return typeof(bool); }
        if (type == typeof(decimal)) { return typeof(decimal); }
        if (type == typeof(double)) { return typeof(double); }
        if (type == typeof(float)) { return typeof(float); }
        if (type == typeof(DateTime)) { return typeof(DateTime); }
        if (type == typeof(DateTimeOffset)) { return typeof(DateTimeOffset); }
        if (type == typeof(TimeSpan)) { return typeof(TimeSpan); }
        if (type == typeof(Guid)) { return typeof(Guid); }
        if (type == typeof(byte[])) { return typeof(byte[]); }
        return typeof(object);
    }
}
