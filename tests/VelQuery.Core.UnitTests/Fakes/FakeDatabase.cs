using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;

namespace VelQuery.Core.UnitTests.Fakes;

/// <summary>
/// In-memory "server" shared by every fake ADO.NET object. Holds scripted results/faults keyed by SQL text and records
/// every object created, every lifecycle event and their global order so tests can assert ownership invariants.
/// Thread-safe so that it can back the parallel-invocation tests.
/// </summary>
internal sealed class FakeDatabase
{
    private readonly Lock _gate = new();
    private readonly List<string> _events = [];
    private readonly List<FakeDbConnection> _connections = [];
    private readonly List<FakeDbCommand> _commands = [];
    private readonly List<FakeDbDataReader> _readers = [];
    private readonly List<FakeDbTransaction> _transactions = [];
    private readonly Dictionary<string, FakeScript> _scripts = new(StringComparer.Ordinal);
    private int _nextId;

    public const string ConnectionString = "Data Source=fake;Password=CONNSTR-SECRET-456";

    /// <summary>Script used for SQL text that has no explicit script.</summary>
    public FakeScript DefaultScript { get; set; } = new();

    /// <summary>Optional dynamic responder; when set it wins over the scripts and may inspect bound parameters.</summary>
    public Func<FakeDbCommand, FakeScript>? Responder { get; set; }

    /// <summary>Fault thrown by <see cref="FakeDbConnection.OpenAsync"/> on factory connections.</summary>
    public Exception? ConnectionOpenFault { get; set; }

    /// <summary>Hook run at the start of every connection open (before the token is checked).</summary>
    public Action? OnConnectionOpen { get; set; }

    /// <summary>Fault thrown (after bookkeeping) when a connection is disposed.</summary>
    public Exception? ConnectionDisposeFault { get; set; }

    /// <summary>Registers <paramref name="script"/> for <paramref name="sql"/> and returns it.</summary>
    public FakeScript Script(string sql, FakeScript script)
    {
        lock (_gate)
        {
            _scripts[sql] = script;
        }

        return script;
    }

    /// <summary>Registers a new script for <paramref name="sql"/> made of <paramref name="results"/>.</summary>
    public FakeScript Script(string sql, params FakeResultSet[] results)
    {
        var script = new FakeScript();
        script.Results.AddRange(results);
        return Script(sql, script);
    }

    internal FakeScript Resolve(FakeDbCommand command)
    {
        if (Responder is { } responder)
        {
            return responder(command);
        }

        lock (_gate)
        {
            return _scripts.TryGetValue(command.CommandText, out var script) ? script : DefaultScript;
        }
    }

    public IReadOnlyList<string> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public IReadOnlyList<FakeDbConnection> Connections => Snapshot(_connections);

    public IReadOnlyList<FakeDbCommand> Commands => Snapshot(_commands);

    public IReadOnlyList<FakeDbDataReader> Readers => Snapshot(_readers);

    public IReadOnlyList<FakeDbTransaction> Transactions => Snapshot(_transactions);

    /// <summary>Creates a connection that is not owned by VelQuery (a caller-owned connection), already open.</summary>
    public FakeDbConnection OpenCallerConnection()
    {
        var connection = new FakeDbConnection(this, NextId(), applyFactoryFaults: false);
        Track(_connections, connection);
        connection.Open();
        return connection;
    }

    internal FakeDbConnection CreateFactoryConnection()
    {
        var connection = new FakeDbConnection(this, NextId(), applyFactoryFaults: true);
        Track(_connections, connection);
        return connection;
    }

    internal int NextId() => Interlocked.Increment(ref _nextId);

    internal void Log(string entry)
    {
        lock (_gate)
        {
            _events.Add(entry);
        }
    }

    internal void Track(FakeDbCommand command) => Track(_commands, command);

    internal void Track(FakeDbDataReader reader) => Track(_readers, reader);

    internal void Track(FakeDbTransaction transaction) => Track(_transactions, transaction);

    /// <summary>Index of the first event equal to <paramref name="entry"/>, or -1.</summary>
    public int IndexOf(string entry)
    {
        lock (_gate)
        {
            return _events.IndexOf(entry);
        }
    }

    /// <summary>
    /// Asserts the PRD §9.1 invariant for VelQuery-owned objects: every factory connection, command and reader was
    /// disposed exactly once and no reader is left open.
    /// </summary>
    public void AssertAllReleasedExactlyOnce()
    {
        foreach (var connection in Connections.Where(c => c.IsFactoryOwned))
        {
            Xunit.Assert.True(connection.DisposeCount == 1, $"{connection} disposed {connection.DisposeCount} time(s).");
        }

        foreach (var command in Commands)
        {
            Xunit.Assert.True(command.DisposeCount == 1, $"{command} disposed {command.DisposeCount} time(s).");
        }

        foreach (var reader in Readers)
        {
            Xunit.Assert.True(reader.DisposeCount == 1, $"{reader} disposed {reader.DisposeCount} time(s).");
            Xunit.Assert.True(reader.IsClosed, $"{reader} left open.");
        }
    }

    private void Track<T>(List<T> list, T item)
    {
        lock (_gate)
        {
            list.Add(item);
        }
    }

    private IReadOnlyList<T> Snapshot<T>(List<T> list)
    {
        lock (_gate)
        {
            return [.. list];
        }
    }
}

/// <summary>Scripted behavior for one SQL text: result sets, affected rows, output values and faults.</summary>
internal sealed class FakeScript
{
    public List<FakeResultSet> Results { get; } = [];

    /// <summary>Value returned by ExecuteNonQuery.</summary>
    public int RecordsAffected { get; set; } = -1;

    /// <summary>Output parameter values (by name without prefix), applied when the command completes.</summary>
    public Dictionary<string, object?> OutputValues { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Exception? ExecuteFault { get; set; }

    /// <summary>Fault thrown by the <see cref="ReadFaultAt"/>-th (1-based) ReadAsync call of a reader.</summary>
    public Exception? ReadFault { get; set; }

    public int ReadFaultAt { get; set; } = 1;

    public Exception? NextResultFault { get; set; }

    public Exception? ReaderDisposeFault { get; set; }

    public Exception? CommandDisposeFault { get; set; }

    /// <summary>Hook invoked at the start of execution (before the token is checked).</summary>
    public Action<FakeDbCommand>? OnExecute { get; set; }

    /// <summary>Hook invoked with the 1-based read number at the start of each read (before the token is checked).</summary>
    public Action<int>? OnRead { get; set; }

    public FakeScript With(params FakeResultSet[] results)
    {
        Results.AddRange(results);
        return this;
    }
}

/// <summary>A result set: column names/types and rows (null cells become <see cref="DBNull"/>).</summary>
internal sealed class FakeResultSet
{
    public FakeResultSet(params (string Name, Type Type)[] columns) => Columns = columns;

    public IReadOnlyList<(string Name, Type Type)> Columns { get; }

    public List<object[]> Rows { get; } = [];

    public FakeResultSet Row(params object?[] values)
    {
        if (values.Length != Columns.Count)
        {
            throw new ArgumentException("row width does not match the column count.", nameof(values));
        }

        Rows.Add([.. values.Select(v => v ?? DBNull.Value)]);
        return this;
    }

    /// <summary>A single-column result set named <paramref name="name"/> holding <paramref name="values"/>.</summary>
    public static FakeResultSet Column<T>(string name, params object?[] values)
    {
        var set = new FakeResultSet((name, typeof(T)));
        foreach (var value in values)
        {
            set.Row(value);
        }

        return set;
    }

    public static FakeResultSet Ints(string name, int count, int start = 1)
    {
        var set = new FakeResultSet((name, typeof(int)));
        for (var i = 0; i < count; i++)
        {
            set.Row(start + i);
        }

        return set;
    }
}

/// <summary>Provider exception with timeout/cancellation flags understood by <see cref="FakeProvider.Classify"/>.</summary>
internal sealed class FakeDbException : DbException
{
    public FakeDbException(string message = "fake provider failure", bool isTimeout = false, bool isCancellation = false)
        : base(message)
    {
        IsTimeout = isTimeout;
        IsCancellation = isCancellation;
    }

    public FakeDbException()
        : this("fake provider failure")
    {
    }

    public FakeDbException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public bool IsTimeout { get; }

    public bool IsCancellation { get; }
}

/// <summary>Immutable copy of a bound parameter taken when the command executed.</summary>
internal sealed record ParameterSnapshot(
    string Name,
    DbType DbType,
    bool DbTypeExplicit,
    int Size,
    byte Precision,
    byte Scale,
    ParameterDirection Direction,
    object? Value);
