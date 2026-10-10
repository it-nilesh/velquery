using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace VelQuery.Core.UnitTests.Fakes;

/// <summary>
/// Fake connection. Mirrors the SqlClient rules that matter for lifecycle tests: one open reader per connection
/// (no MARS), commands must carry the connection's pending transaction, and only one local transaction at a time.
/// </summary>
internal sealed class FakeDbConnection : DbConnection
{
    private readonly FakeDatabase _db;
    private readonly bool _applyFactoryFaults;
    private ConnectionState _state = ConnectionState.Closed;
    private int _openCount;
    private int _closeCount;
    private int _disposeCount;

    internal FakeDbConnection(FakeDatabase db, int id, bool applyFactoryFaults)
    {
        _db = db;
        Id = id;
        _applyFactoryFaults = applyFactoryFaults;
        IsFactoryOwned = applyFactoryFaults;
    }

    public int Id { get; }

    /// <summary>True when created by <see cref="FakeConnectionFactory"/> (VelQuery-owned).</summary>
    public bool IsFactoryOwned { get; }

    public int OpenCount => Volatile.Read(ref _openCount);

    public int CloseCount => Volatile.Read(ref _closeCount);

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    internal FakeDbDataReader? OpenReader { get; set; }

    internal FakeDbTransaction? ActiveTransaction { get; set; }

    [AllowNull]
    public override string ConnectionString { get; set; } = FakeDatabase.ConnectionString;

    public override string Database => "fake";

    public override string DataSource => "fake";

    public override string ServerVersion => "1.0";

    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Open() => OpenCore(CancellationToken.None);

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        OpenCore(cancellationToken);
        return Task.CompletedTask;
    }

    private void OpenCore(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(DisposeCount > 0, this);
        if (_state == ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection is already open.");
        }

        if (_applyFactoryFaults)
        {
            _db.OnConnectionOpen?.Invoke();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_applyFactoryFaults && _db.ConnectionOpenFault is { } fault)
        {
            throw fault;
        }

        _state = ConnectionState.Open;
        Interlocked.Increment(ref _openCount);
        _db.Log($"{this}:Open");
    }

    public override void Close()
    {
        if (_state != ConnectionState.Open)
        {
            return;
        }

        _state = ConnectionState.Closed;
        Interlocked.Increment(ref _closeCount);
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
            if (_applyFactoryFaults && _db.ConnectionDisposeFault is { } fault)
            {
                throw fault;
            }

            return;
        }

        base.Dispose(disposing);
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        EnsureOpen();
        if (ActiveTransaction is not null)
        {
            throw new InvalidOperationException("Parallel transactions are not supported.");
        }

        var transaction = new FakeDbTransaction(_db, this, _db.NextId(), isolationLevel);
        _db.Track(transaction);
        ActiveTransaction = transaction;
        _db.Log($"{transaction}:Begin");
        return transaction;
    }

    protected override DbCommand CreateDbCommand()
    {
        var command = new FakeDbCommand(_db, _db.NextId()) { Connection = this };
        _db.Track(command);
        _db.Log($"{command}:Create");
        return command;
    }

    internal void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(DisposeCount > 0, this);
        if (_state != ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection is not open.");
        }
    }

    public override string ToString() => $"Connection{Id}";
}
