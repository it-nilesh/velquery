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
            return owner.Result.CreateDataReader();
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
