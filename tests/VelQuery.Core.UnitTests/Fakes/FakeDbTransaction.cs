using System;
using System.Data;
using System.Data.Common;

namespace VelQuery.Core.UnitTests.Fakes;

/// <summary>Fake local transaction. Like real drivers, disposing an uncompleted transaction rolls it back.</summary>
internal sealed class FakeDbTransaction : DbTransaction
{
    private readonly FakeDatabase _db;
    private readonly FakeDbConnection _connection;
    private bool _completed;

    internal FakeDbTransaction(FakeDatabase db, FakeDbConnection connection, int id, IsolationLevel isolationLevel)
    {
        _db = db;
        _connection = connection;
        Id = id;
        IsolationLevel = isolationLevel;
    }

    public int Id { get; }

    public int CommitCount { get; private set; }

    public int RollbackCount { get; private set; }

    public int DisposeCount { get; private set; }

    public bool RolledBackOnDispose { get; private set; }

    /// <summary>True when the transaction was rolled back explicitly or by disposal.</summary>
    public bool WasRolledBack => RollbackCount > 0 || RolledBackOnDispose;

    public Exception? CommitFault { get; set; }

    public override IsolationLevel IsolationLevel { get; }

    protected override DbConnection DbConnection => _connection;

    public override void Commit()
    {
        EnsureActive();
        CommitCount++;
        _db.Log($"{this}:Commit");
        if (CommitFault is { } fault)
        {
            throw fault;
        }

        End();
    }

    public override void Rollback()
    {
        EnsureActive();
        RollbackCount++;
        _db.Log($"{this}:Rollback");
        End();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeCount++;
            _db.Log($"{this}:Dispose");
            if (!_completed)
            {
                RolledBackOnDispose = true;
                End();
            }
        }

        base.Dispose(disposing);
    }

    private void EnsureActive()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The transaction has completed.");
        }
    }

    private void End()
    {
        _completed = true;
        if (ReferenceEquals(_connection.ActiveTransaction, this))
        {
            _connection.ActiveTransaction = null;
        }
    }

    public override string ToString() => $"Transaction{Id}";
}
