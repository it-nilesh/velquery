using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Execution;

namespace VelQuery;

/// <summary>
/// Several commands sent to the database in one round trip (<see cref="System.Data.Common.DbBatch"/>). Create one with
/// <see cref="VelQuerySession.CreateBatch"/>, add commands, then call <see cref="ExecuteAsync"/> once.
/// </summary>
/// <remarks>
/// <para><b>Round trips.</b> SQL Server and PostgreSQL send the whole batch at once. Other providers run the commands one
/// after another on the same connection, with the same results: SQLite runs in-process (no round trip to save), and
/// MySqlConnector does not report per-command row counts for batches, which VelQuery never guesses.</para>
/// <para><b>Atomicity.</b> A batch is not a transaction: when a command fails, commands before it may already have taken
/// effect. Run the batch inside <see cref="VelQuerySession.BeginTransactionAsync"/> for all-or-nothing.</para>
/// <para><b>Results.</b> <see cref="ExecuteAsync"/> returns each command's affected-row count exactly as the driver reports
/// it (-1 when it reports none); VelQuery never invents counts. Output parameters are populated after it completes.</para>
/// <para><b>Errors.</b> A failure carries the query ID of the failing command when it is known (always when commands run
/// one at a time; for a one-round-trip batch only if the driver reports it), otherwise the batch's query ID. Nothing is
/// retried.</para>
/// </remarks>
public sealed class VelQueryBatch
{
    private readonly VelQuerySession _session;
    private readonly List<BatchItem> _items = [];
    private bool _executed;

    internal VelQueryBatch(VelQuerySession session, string queryId)
    {
        _session = session;
        QueryId = queryId;
    }

    /// <summary>Query ID used for errors not attributable to one command.</summary>
    public string QueryId { get; }

    /// <summary>Number of commands added.</summary>
    public int Count => _items.Count;

    /// <summary>Adds a SQL command with bound parameters (never concatenated into the SQL).</summary>
    /// <param name="sql">The SQL text.</param>
    /// <param name="parameters">Its parameters, or null.</param>
    /// <param name="queryId">Query ID for errors from this command; defaults to the batch's ID plus its position.</param>
    public VelQueryBatch Add(string sql, QueryParameters? parameters = null, string? queryId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ThrowIfExecuted();
        _items.Add(new BatchItem(new TextSpec(sql, parameters, queryId ?? QueryId + "#" + _items.Count, null, CommandType.Text), null));
        return this;
    }

    /// <summary>Adds a command described by a <see cref="QueryDescriptor{TArgs}"/> with its arguments.</summary>
    public VelQueryBatch Add<TArgs>(QueryDescriptor<TArgs> command, TArgs args)
    {
        ArgumentNullException.ThrowIfNull(command);
        ThrowIfExecuted();
        if (command.CommandType != CommandType.Text)
        {
            throw new ArgumentException("A batch holds SQL text commands; call stored procedures individually.", nameof(command));
        }

        _items.Add(new BatchItem(null, new DescriptorItem<TArgs>(command, args)));
        return this;
    }

    /// <summary>Executes every command in one round trip and returns each command's affected-row count, in order.</summary>
    public Task<IReadOnlyList<int>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfExecuted();
        _executed = true;
        return _session.ExecuteBatchAsync(this, _items, cancellationToken);
    }

    private void ThrowIfExecuted()
    {
        if (_executed)
        {
            throw new InvalidOperationException("This batch has already been executed; create a new one with CreateBatch.");
        }
    }

    /// <summary>One command: runtime SQL (<see cref="Text"/>) or a descriptor with arguments (<see cref="Descriptor"/>).</summary>
    internal readonly record struct BatchItem(TextSpec? Text, IDescriptorItem? Descriptor)
    {
        public string QueryId => Text?.QueryId ?? Descriptor!.QueryId;
    }

    /// <summary>A descriptor and its arguments, without exposing the argument type to the batch.</summary>
    internal interface IDescriptorItem
    {
        string QueryId { get; }

        System.Data.Common.DbCommand CreateCommand(VelQuerySession session, System.Data.Common.DbConnection connection);
    }

    private sealed class DescriptorItem<TArgs>(QueryDescriptor<TArgs> command, TArgs args) : IDescriptorItem
    {
        public string QueryId => command.QueryId;

        public System.Data.Common.DbCommand CreateCommand(VelQuerySession session, System.Data.Common.DbConnection connection)
            => session.CreateCommand(connection, new DescriptorSpec<TArgs>(command, args));
    }
}
