using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.Data.SqlClient;
using VelQuery.Parameters;
using VelQuery.Providers;

namespace VelQuery.Generators.RuntimeTests;

/// <summary>A snapshot of a bound parameter, taken when the command executes.</summary>
internal sealed record BoundParameter(string Name, DbType DbType, int Size, byte Precision, byte Scale, object? Value);

/// <summary>A snapshot of an executed command.</summary>
internal sealed record ExecutedCommand(string Text, CommandType Type, int Timeout, IReadOnlyList<BoundParameter> Parameters);

/// <summary>
/// In-memory connection whose commands return <see cref="DataTableReader"/> (a BCL <see cref="DbDataReader"/>) over
/// a configured table. Parameters are real <see cref="SqlParameter"/>s so DbType/size semantics match SqlClient.
/// </summary>
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test double; no database.")]
internal sealed class FakeConnection : DbConnection
{
    public DataTable Result { get; set; } = new();

    public int AffectedRows { get; set; }

    public List<ExecutedCommand> Executed { get; } = [];

    /// <summary>Behaviors passed to ExecuteReader, in order.</summary>
    public List<CommandBehavior> Behaviors { get; } = [];

    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    public override string Database => "fake";

    public override string DataSource => "fake";

    public override string ServerVersion => "1.0";

    public override ConnectionState State => ConnectionState.Open;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Close()
    {
    }

    public override void Open()
    {
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

    protected override DbCommand CreateDbCommand() => new FakeCommand(this);

    private sealed class FakeCommand(FakeConnection owner) : DbCommand
    {
        private readonly SqlCommand _parameters = new();

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; } = CommandType.Text;

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => _parameters.Parameters;

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery()
        {
            Record();
            return owner.AffectedRows;
        }

        public override object? ExecuteScalar()
        {
            Record();
            return owner.Result.Rows.Count == 0 ? null : owner.Result.Rows[0][0];
        }

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => new SqlParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Record();
            owner.Behaviors.Add(behavior);
            var reader = owner.Result.CreateDataReader();
            return (behavior & CommandBehavior.SequentialAccess) != 0 ? new SequentialEnforcingReader(reader) : reader;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _parameters.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Record()
            => owner.Executed.Add(new ExecutedCommand(CommandText, CommandType, CommandTimeout,
                Parameters.Cast<DbParameter>().Select(p => new BoundParameter(p.ParameterName, p.DbType, p.Size, p.Precision, p.Scale, p.Value)).ToList()));
    }
}

/// <summary>
/// Enforces <see cref="CommandBehavior.SequentialAccess"/> the way SqlClient does: within a row, columns must be read in
/// increasing ordinal order (re-reading the current column, e.g. IsDBNull then a getter, is allowed).
/// </summary>
internal sealed class SequentialEnforcingReader(DbDataReader inner) : DbDataReader
{
    private int _last = -1;

    private int Consume(int ordinal)
    {
        if (ordinal < _last)
        {
            throw new InvalidOperationException($"SequentialAccess violation: column {ordinal} read after column {_last}.");
        }

        _last = ordinal;
        return ordinal;
    }

    public override bool Read()
    {
        _last = -1;
        return inner.Read();
    }

    public override bool NextResult()
    {
        _last = -1;
        return inner.NextResult();
    }

    public override object this[int ordinal] => inner[Consume(ordinal)];

    public override object this[string name] => this[GetOrdinal(name)];

    public override int Depth => inner.Depth;

    public override int FieldCount => inner.FieldCount;

    public override bool HasRows => inner.HasRows;

    public override bool IsClosed => inner.IsClosed;

    public override int RecordsAffected => inner.RecordsAffected;

    public override bool GetBoolean(int ordinal) => inner.GetBoolean(Consume(ordinal));

    public override byte GetByte(int ordinal) => inner.GetByte(Consume(ordinal));

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(Consume(ordinal), dataOffset, buffer, bufferOffset, length);

    public override char GetChar(int ordinal) => inner.GetChar(Consume(ordinal));

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(Consume(ordinal), dataOffset, buffer, bufferOffset, length);

    public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);

    public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(Consume(ordinal));

    public override decimal GetDecimal(int ordinal) => inner.GetDecimal(Consume(ordinal));

    public override double GetDouble(int ordinal) => inner.GetDouble(Consume(ordinal));

    public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);

    public override T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(Consume(ordinal));

    public override float GetFloat(int ordinal) => inner.GetFloat(Consume(ordinal));

    public override Guid GetGuid(int ordinal) => inner.GetGuid(Consume(ordinal));

    public override short GetInt16(int ordinal) => inner.GetInt16(Consume(ordinal));

    public override int GetInt32(int ordinal) => inner.GetInt32(Consume(ordinal));

    public override long GetInt64(int ordinal) => inner.GetInt64(Consume(ordinal));

    public override string GetName(int ordinal) => inner.GetName(ordinal);

    public override int GetOrdinal(string name) => inner.GetOrdinal(name);

    public override string GetString(int ordinal) => inner.GetString(Consume(ordinal));

    public override object GetValue(int ordinal) => inner.GetValue(Consume(ordinal));

    public override int GetValues(object[] values) => throw new NotSupportedException();

    public override bool IsDBNull(int ordinal) => inner.IsDBNull(Consume(ordinal));

    public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Minimal SQL Server-style provider.</summary>
internal sealed class TestProvider : IVelQueryProvider
{
    public string DialectId => "test";

    public ProviderCapabilities Capabilities => ProviderCapabilities.None;

    public string FormatParameterName(string name) => "@" + name;

    public void ConfigureParameter(DbParameter parameter, in ParameterSpec spec, object? value)
    {
    }

    public string QuoteIdentifier(ReadOnlySpan<char> validatedIdentifier) => "[" + validatedIdentifier.ToString() + "]";

    public ProviderErrorKind Classify(Exception exception) => ProviderErrorKind.Other;
}

internal static class Tables
{
    public static DataTable Create(params (string Name, Type Type)[] columns)
    {
        var table = new DataTable();
        foreach (var (name, type) in columns)
        {
            table.Columns.Add(name, type);
        }

        return table;
    }

    public static DataTable WithRows(this DataTable table, params object?[][] rows)
    {
        foreach (var row in rows)
        {
            table.Rows.Add(row.Select(v => v ?? DBNull.Value).ToArray());
        }

        return table;
    }
}
