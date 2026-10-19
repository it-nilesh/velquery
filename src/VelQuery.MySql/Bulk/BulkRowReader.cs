using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace VelQuery.MySql.Bulk;

/// <summary>
/// Streams source rows to <see cref="MySqlConnector.MySqlBulkCopy"/> as a forward-only <see cref="DbDataReader"/>, so client
/// memory stays bounded to one row. MySqlBulkCopy pulls rows with <see cref="ReadAsync"/> and reads them with
/// <see cref="GetValues"/>; synchronous sources complete <see cref="ReadAsync"/> synchronously (cached tasks, no
/// continuation per row), and asynchronous sources are awaited only when <c>MoveNextAsync</c> does not complete inline.
/// </summary>
internal sealed class BulkRowReader<T> : DbDataReader
{
    private readonly MySqlBulkInsertPlan<T> _plan;
    private readonly IEnumerator<T>? _rows;
    private readonly IAsyncEnumerator<T>? _asyncRows;
    private readonly object[] _current;
    private bool _closed;
    private bool _disposed;

    public BulkRowReader(MySqlBulkInsertPlan<T> plan, IEnumerable<T> rows)
    {
        _plan = plan;
        _rows = rows.GetEnumerator();
        _current = new object[plan.Columns.Length];
    }

    public BulkRowReader(MySqlBulkInsertPlan<T> plan, IAsyncEnumerable<T> rows, CancellationToken cancellationToken)
    {
        _plan = plan;
        _asyncRows = rows.GetAsyncEnumerator(cancellationToken);
        _current = new object[plan.Columns.Length];
    }

    /// <summary>Rows read from the source so far.</summary>
    public long RowsRead { get; private set; }

    /// <summary>
    /// The exception thrown by the caller's source or getters, if any. MySqlBulkCopy wraps it in a MySqlException; the load
    /// rethrows the original so caller failures are not reported as database failures.
    /// </summary>
    public ExceptionDispatchInfo? SourceFailure { get; private set; }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_rows is not null)
            {
                return _rows.MoveNext() ? Stage(_rows.Current) : Task.FromResult(false);
            }

            var next = _asyncRows!.MoveNextAsync();
            if (next.IsCompletedSuccessfully)
            {
                return next.Result ? Stage(_asyncRows.Current) : Task.FromResult(false);
            }

            return AwaitNextAsync(next);
        }
        catch (Exception ex)
        {
            SourceFailure = ExceptionDispatchInfo.Capture(ex);
            throw;
        }
    }

    private async Task<bool> AwaitNextAsync(ValueTask<bool> next)
    {
        try
        {
            if (!await next.ConfigureAwait(false))
            {
                return false;
            }

            await Stage(_asyncRows!.Current).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            SourceFailure = ExceptionDispatchInfo.Capture(ex);
            throw;
        }
    }

    private Task<bool> Stage(T row)
    {
        var columns = _plan.Columns;
        for (var i = 0; i < columns.Length; i++)
        {
            _current[i] = columns[i].Getter(row) ?? DBNull.Value;
        }

        RowsRead++;
        return Task.FromResult(true);
    }

    public override bool Read()
    {
        if (_rows is null)
        {
            throw new NotSupportedException("Asynchronous row sources must be read with ReadAsync.");
        }

        return _rows.MoveNext() && Stage(_rows.Current).Result;
    }

    public override int FieldCount => _current.Length;

    public override object GetValue(int ordinal) => _current[ordinal];

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, _current.Length);
        Array.Copy(_current, values, count);
        return count;
    }

    public override bool IsDBNull(int ordinal) => _current[ordinal] is DBNull;

    public override string GetName(int ordinal) => _plan.Columns[ordinal].Name;

    public override int GetOrdinal(string name)
    {
        var columns = _plan.Columns;
        for (var i = 0; i < columns.Length; i++)
        {
            if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(name), name, "No such column.");
    }

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    public override Type GetFieldType(int ordinal) => typeof(object);

    public override string GetDataTypeName(int ordinal) => _plan.Columns[ordinal].ValueType.Name;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override int Depth => 0;

    public override bool HasRows => true;

    public override bool IsClosed => _closed;

    public override int RecordsAffected => -1;

    public override bool NextResult() => false;

    public override bool GetBoolean(int ordinal) => (bool)_current[ordinal];

    public override byte GetByte(int ordinal) => (byte)_current[ordinal];

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override char GetChar(int ordinal) => (char)_current[ordinal];

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override DateTime GetDateTime(int ordinal) => (DateTime)_current[ordinal];

    public override decimal GetDecimal(int ordinal) => (decimal)_current[ordinal];

    public override double GetDouble(int ordinal) => (double)_current[ordinal];

    public override float GetFloat(int ordinal) => (float)_current[ordinal];

    public override Guid GetGuid(int ordinal) => (Guid)_current[ordinal];

    public override short GetInt16(int ordinal) => (short)_current[ordinal];

    public override int GetInt32(int ordinal) => (int)_current[ordinal];

    public override long GetInt64(int ordinal) => (long)_current[ordinal];

    public override string GetString(int ordinal) => (string)_current[ordinal];

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();

    public override void Close() => _closed = true;

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = _closed = true;
            _rows?.Dispose();
            if (_asyncRows is not null)
            {
                // Synchronous disposal is only reached if DisposeAsync was not used; block rather than leak the source.
                _asyncRows.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = _closed = true;
            _rows?.Dispose();
            if (_asyncRows is not null)
            {
                await _asyncRows.DisposeAsync().ConfigureAwait(false);
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
