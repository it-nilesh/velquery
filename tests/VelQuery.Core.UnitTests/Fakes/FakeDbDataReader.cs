using System;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VelQuery.Core.UnitTests.Fakes;

/// <summary>
/// Reader over a <see cref="FakeScript"/>. Unlike <see cref="System.Data.DataTableReader"/> it gives full control over
/// faults and disposal, counts reads, honors tokens and applies output parameter values only when closed.
/// </summary>
internal sealed class FakeDbDataReader : DbDataReader
{
    private readonly FakeDatabase _db;
    private readonly FakeDbCommand _command;
    private readonly FakeScript _script;
    private int _resultIndex;
    private int _rowIndex = -1;
    private int _readCalls;
    private int _disposeCount;
    private bool _closed;

    internal FakeDbDataReader(FakeDatabase db, FakeDbCommand command, FakeScript script, int id, CommandBehavior behavior)
    {
        _db = db;
        _command = command;
        _script = script;
        Id = id;
        Behavior = behavior;
    }

    public int Id { get; }

    public CommandBehavior Behavior { get; }

    /// <summary>Total Read/ReadAsync calls across all result sets.</summary>
    public int ReadCalls => _readCalls;

    /// <summary>Number of rows actually returned (Read returned true).</summary>
    public int RowsReturned { get; private set; }

    public int NextResultCalls { get; private set; }

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    private FakeResultSet? Current => _resultIndex < _script.Results.Count ? _script.Results[_resultIndex] : null;

    public override int FieldCount => Current?.Columns.Count ?? 0;

    public override bool HasRows => Current?.Rows.Count > 0;

    public override bool IsClosed => _closed;

    public override int RecordsAffected => -1;

    public override int Depth => 0;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read() => ReadCore(CancellationToken.None);

    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(ReadCore(cancellationToken));

    private bool ReadCore(CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        var call = ++_readCalls;
        _script.OnRead?.Invoke(call);
        cancellationToken.ThrowIfCancellationRequested();
        if (_script.ReadFault is { } fault && call == _script.ReadFaultAt)
        {
            throw fault;
        }

        var set = Current;
        if (set is null)
        {
            return false;
        }

        if (Behavior.HasFlag(CommandBehavior.SingleRow) && _rowIndex >= 0)
        {
            _rowIndex = set.Rows.Count;
            return false;
        }

        _rowIndex++;
        if (_rowIndex < set.Rows.Count)
        {
            RowsReturned++;
            return true;
        }

        _rowIndex = set.Rows.Count;
        return false;
    }

    public override bool NextResult() => NextResultCore(CancellationToken.None);

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(NextResultCore(cancellationToken));

    private bool NextResultCore(CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        NextResultCalls++;
        cancellationToken.ThrowIfCancellationRequested();
        if (_script.NextResultFault is { } fault)
        {
            throw fault;
        }

        if (_resultIndex < _script.Results.Count)
        {
            _resultIndex++;
        }

        _rowIndex = -1;
        return _resultIndex < _script.Results.Count;
    }

    public override string GetName(int ordinal) => Column(ordinal).Name;

    public override int GetOrdinal(string name)
    {
        var set = Current ?? throw new InvalidOperationException("No result set.");
        for (var i = 0; i < set.Columns.Count; i++)
        {
            if (string.Equals(set.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(name), name, "unknown column");
    }

    public override Type GetFieldType(int ordinal) => Column(ordinal).Type;

    public override string GetDataTypeName(int ordinal) => Column(ordinal).Type.Name;

    public override object GetValue(int ordinal)
    {
        ThrowIfClosed();
        var set = Current ?? throw new InvalidOperationException("No result set.");
        if (_rowIndex < 0 || _rowIndex >= set.Rows.Count)
        {
            throw new InvalidOperationException("Invalid attempt to read when no data is present.");
        }

        _ = Column(ordinal);
        return set.Rows[_rowIndex][ordinal];
    }

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);

    public override char GetChar(int ordinal) => (char)GetValue(ordinal);

    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);

    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);

    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);

    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);

    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);

    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);

    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);

    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override IEnumerator GetEnumerator() => new DbEnumerator(this);

    public override void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _command.ApplyOutputs();
        if (_command.Connection is FakeDbConnection connection && ReferenceEquals(connection.OpenReader, this))
        {
            connection.OpenReader = null;
        }

        _db.Log($"{this}:Close");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Increment(ref _disposeCount);
            _db.Log($"{this}:Dispose");
            Close();
            base.Dispose(disposing);
            if (_script.ReaderDisposeFault is { } fault)
            {
                throw fault;
            }

            return;
        }

        base.Dispose(disposing);
    }

    private (string Name, Type Type) Column(int ordinal)
    {
        var set = Current ?? throw new InvalidOperationException("No result set.");
        if (ordinal < 0 || ordinal >= set.Columns.Count)
        {
            throw new ArgumentOutOfRangeException(ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return set.Columns.ElementAt(ordinal);
    }

    private void ThrowIfClosed()
    {
        if (_closed)
        {
            throw new InvalidOperationException("The reader is closed.");
        }
    }

    public override string ToString() => $"Reader{Id}";
}
