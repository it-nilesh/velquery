using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VelQuery.Core.UnitTests.Fakes;

/// <summary>Fake command that records parameters, transaction, timeout and behavior at execution time.</summary>
internal sealed class FakeDbCommand : DbCommand
{
    private readonly FakeDatabase _db;
    private readonly FakeParameterCollection _parameters = new();
    private int _cancelCount;
    private int _disposeCount;

    internal FakeDbCommand(FakeDatabase db, int id)
    {
        _db = db;
        Id = id;
    }

    public int Id { get; }

    public int CancelCount => Volatile.Read(ref _cancelCount);

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>"Reader", "NonQuery" or "Scalar" once executed.</summary>
    public string? ExecutedAs { get; private set; }

    public CommandBehavior? Behavior { get; private set; }

    public DbTransaction? TransactionAtExecute { get; private set; }

    public int TimeoutAtExecute { get; private set; }

    public IReadOnlyList<ParameterSnapshot> BoundParameters { get; private set; } = [];

    internal FakeScript? Script { get; private set; }

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; } = 30;

    public override CommandType CommandType { get; set; } = CommandType.Text;

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection { get; set; }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel()
    {
        Interlocked.Increment(ref _cancelCount);
        _db.Log($"{this}:Cancel");
    }

    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => new FakeDbParameter();

    public override int ExecuteNonQuery() => ExecuteNonQueryAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override object? ExecuteScalar() => ExecuteScalarAsync(CancellationToken.None).GetAwaiter().GetResult();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var script = BeginExecute("NonQuery", CommandBehavior.Default, cancellationToken);
        ApplyOutputs();
        return Task.FromResult(script.RecordsAffected);
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var script = BeginExecute("Scalar", CommandBehavior.Default, cancellationToken);
        object? value = script.Results.Count > 0 && script.Results[0].Rows.Count > 0 && script.Results[0].Columns.Count > 0
            ? script.Results[0].Rows[0][0]
            : null;
        ApplyOutputs();
        return Task.FromResult(value);
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        var script = BeginExecute("Reader", behavior, cancellationToken);
        var connection = (FakeDbConnection)Connection!;
        var reader = new FakeDbDataReader(_db, this, script, _db.NextId(), behavior);
        _db.Track(reader);
        connection.OpenReader = reader;
        _db.Log($"{reader}:Open");
        return Task.FromResult<DbDataReader>(reader);
    }

    private FakeScript BeginExecute(string kind, CommandBehavior behavior, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(DisposeCount > 0, this);
        BoundParameters = [.. _parameters.Items.Select(p => p.Snapshot())];
        var script = _db.Resolve(this);
        Script = script;
        script.OnExecute?.Invoke(this);
        cancellationToken.ThrowIfCancellationRequested();

        if (Connection is not FakeDbConnection connection)
        {
            throw new InvalidOperationException("The command has no fake connection.");
        }

        connection.EnsureOpen();
        if (connection.OpenReader is not null)
        {
            throw new InvalidOperationException("There is already an open DataReader associated with this connection.");
        }

        if (connection.ActiveTransaction is { } pending && !ReferenceEquals(Transaction, pending))
        {
            throw new InvalidOperationException("The command requires the connection's pending transaction.");
        }

        if (Transaction is { } transaction && !ReferenceEquals(transaction.Connection, connection))
        {
            throw new InvalidOperationException("The transaction belongs to another connection.");
        }

        ExecutedAs = kind;
        Behavior = behavior;
        TransactionAtExecute = Transaction;
        TimeoutAtExecute = CommandTimeout;
        _db.Log($"{this}:Execute");
        if (script.ExecuteFault is { } fault)
        {
            throw fault;
        }

        return script;
    }

    /// <summary>Copies scripted output values into Output/InputOutput/ReturnValue parameters (on completion).</summary>
    internal void ApplyOutputs()
    {
        if (Script is null)
        {
            return;
        }

        foreach (var parameter in _parameters.Items)
        {
            if (parameter.Direction == ParameterDirection.Input)
            {
                continue;
            }

            if (Script.OutputValues.TryGetValue(parameter.ParameterName.TrimStart('@'), out var value))
            {
                parameter.Value = value ?? DBNull.Value;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Increment(ref _disposeCount);
            _db.Log($"{this}:Dispose");
            base.Dispose(disposing);
            if ((Script ?? _db.Resolve(this)).CommandDisposeFault is { } fault)
            {
                throw fault;
            }

            return;
        }

        base.Dispose(disposing);
    }

    public override string ToString() => $"Command{Id}";
}

internal sealed class FakeDbParameter : DbParameter
{
    private DbType _dbType = DbType.String;

    public bool DbTypeExplicit { get; private set; }

    public override DbType DbType
    {
        get => _dbType;
        set
        {
            _dbType = value;
            DbTypeExplicit = true;
        }
    }

    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;

    public override int Size { get; set; }

    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;

    public override bool SourceColumnNullMapping { get; set; }

    public override object? Value { get; set; }

    public override byte Precision { get; set; }

    public override byte Scale { get; set; }

    public override void ResetDbType()
    {
        _dbType = DbType.String;
        DbTypeExplicit = false;
    }

    internal ParameterSnapshot Snapshot()
        => new(ParameterName, DbType, DbTypeExplicit, Size, Precision, Scale, Direction, Value);
}

internal sealed class FakeParameterCollection : DbParameterCollection
{
    private readonly List<FakeDbParameter> _items = [];

    internal IReadOnlyList<FakeDbParameter> Items => _items;

    public override int Count => _items.Count;

    public override object SyncRoot => _items;

    public override int Add(object value)
    {
        _items.Add((FakeDbParameter)value);
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values)
        {
            Add(value!);
        }
    }

    public override void Clear() => _items.Clear();

    public override bool Contains(object value) => _items.Contains((FakeDbParameter)value);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _items.GetEnumerator();

    public override int IndexOf(object value) => _items.IndexOf((FakeDbParameter)value);

    public override int IndexOf(string parameterName)
        => _items.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));

    public override void Insert(int index, object value) => _items.Insert(index, (FakeDbParameter)value);

    public override void Remove(object value) => _items.Remove((FakeDbParameter)value);

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];

    protected override void SetParameter(int index, DbParameter value) => _items[index] = (FakeDbParameter)value;

    protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = (FakeDbParameter)value;
}
