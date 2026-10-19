using System;
using System.Collections.Immutable;
using System.Text;
using Microsoft.Data.Sqlite;

namespace VelQuery.Sqlite.Bulk;

/// <summary>
/// Immutable description of a SQLite bulk load: destination table, typed column getters and options. Build it once
/// and reuse it; it is thread-safe and uses no reflection.
/// </summary>
/// <remarks>
/// SQLite has no bulk-load API. The load runs one prepared single-row <c>INSERT</c> per row inside one transaction,
/// which is the fastest correct path through Microsoft.Data.Sqlite: multi-row <c>VALUES</c> batches were measured
/// slower (parameters are bound by name, and SQLite's name lookup is linear in the parameter count).
/// </remarks>
/// <example>
/// <code>
/// static readonly SqliteBulkInsertPlan&lt;Product&gt; Plan = SqliteBulkInsertPlan&lt;Product&gt;.Create("products")
///     .Column("name", p => p.Name)
///     .Column("price", p => p.Price)
///     .Column("id", p => p.Id, SqliteType.Blob); // e.g. a Guid stored as 16 bytes instead of TEXT
/// </code>
/// </example>
public sealed class SqliteBulkInsertPlan<T>
{
    private SqliteBulkInsertPlan(string table, string quotedTable, ImmutableArray<SqliteBulkColumn<T>> columns, SqliteBulkInsertOptions options, string queryId)
    {
        Table = table;
        QuotedTable = quotedTable;
        Columns = columns;
        Options = options;
        QueryId = queryId;
    }

    /// <summary>Destination table as supplied, for example <c>main.products</c>.</summary>
    public string Table { get; }

    /// <summary>Destination table quoted by the provider, for example <c>"main"."products"</c>.</summary>
    public string QuotedTable { get; }

    /// <summary>Mapped columns in parameter order.</summary>
    public ImmutableArray<SqliteBulkColumn<T>> Columns { get; }

    /// <summary>Load options.</summary>
    public SqliteBulkInsertOptions Options { get; }

    /// <summary>Diagnostic identity used in errors (defaults to <c>bulk:&lt;table&gt;</c>).</summary>
    public string QueryId { get; }

    /// <summary>The prepared statement this plan executes once per row; parameters are <c>@p0</c>, <c>@p1</c>, …</summary>
    public string InsertCommand
    {
        get
        {
            var sql = new StringBuilder("INSERT INTO ").Append(QuotedTable).Append(" (");
            for (var i = 0; i < Columns.Length; i++)
            {
                sql.Append(i == 0 ? string.Empty : ", ").Append(SqliteProvider.Instance.QuoteIdentifier(Columns[i].Name));
            }

            sql.Append(") VALUES (");
            for (var i = 0; i < Columns.Length; i++)
            {
                sql.Append(i == 0 ? "@p" : ", @p").Append(i);
            }

            return sql.Append(')').ToString();
        }
    }

    /// <summary>
    /// Starts a plan for <paramref name="table"/> (<c>table</c> or <c>schema.table</c>, where the schema is <c>main</c>,
    /// <c>temp</c> or an attached database). Each part must be a plain identifier (letters, digits, underscore); names
    /// are quoted, never interpolated raw (VQ-FR-042).
    /// </summary>
    public static SqliteBulkInsertPlan<T> Create(string table, SqliteBulkInsertOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        var parts = table.Split('.');
        if (parts.Length > 2)
        {
            throw new ArgumentException("Use 'table' or 'schema.table'.", nameof(table));
        }

        var quoted = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            ValidateIdentifier(parts[i], nameof(table));
            quoted[i] = SqliteProvider.Instance.QuoteIdentifier(parts[i]);
        }

        return new SqliteBulkInsertPlan<T>(table, string.Join('.', quoted), [], options ?? new SqliteBulkInsertOptions(), "bulk:" + table);
    }

    /// <summary>
    /// Returns a copy that also writes <paramref name="getter"/> to column <paramref name="column"/>. The value is bound
    /// like a query parameter, so it is stored in the representation Microsoft.Data.Sqlite's typed getters read back
    /// (for example TEXT for <see cref="DateTime"/>); pass <paramref name="type"/> to choose another storage class.
    /// </summary>
    public SqliteBulkInsertPlan<T> Column<TValue>(string column, Func<T, TValue> getter, SqliteType? type = null)
    {
        ValidateIdentifier(column, nameof(column));
        ArgumentNullException.ThrowIfNull(getter);
        if ((Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue)).IsEnum)
        {
            // Keeps the stored value explicit: an enum is written as its number, never its name.
            throw new ArgumentException($"Column '{column}': map enums to their underlying value, e.g. r => (int)r.Status.", nameof(getter));
        }

        foreach (var existing in Columns)
        {
            // SQLite column names are case-insensitive.
            if (string.Equals(existing.Name, column, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Column '{column}' is already mapped.", nameof(column));
            }
        }

        return new SqliteBulkInsertPlan<T>(Table, QuotedTable, Columns.Add(new SqliteBulkColumn<T>(column, type, row => getter(row))), Options, QueryId);
    }

    /// <summary>Returns a copy with different options.</summary>
    public SqliteBulkInsertPlan<T> WithOptions(SqliteBulkInsertOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SqliteBulkInsertPlan<T>(Table, QuotedTable, Columns, options, QueryId);
    }

    /// <summary>Returns a copy with a different diagnostic identity.</summary>
    public SqliteBulkInsertPlan<T> WithQueryId(string queryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryId);
        return new SqliteBulkInsertPlan<T>(Table, QuotedTable, Columns, Options, queryId);
    }

    private static void ValidateIdentifier(string value, string parameter)
    {
        if (string.IsNullOrEmpty(value) || value.Length > SqliteProvider.MaxIdentifierLength || char.IsDigit(value[0]))
        {
            throw new ArgumentException($"'{value}' is not a valid SQLite identifier.", parameter);
        }

        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                throw new ArgumentException($"'{value}' is not a valid SQLite identifier.", parameter);
            }
        }
    }
}

/// <summary>A mapped bulk column.</summary>
/// <param name="Name">Destination column name (quoted as given).</param>
/// <param name="Type">Storage class override, or <see langword="null"/> to let the driver infer it from the value.</param>
/// <param name="Getter">Reads the value from a row; <see langword="null"/> is written as NULL.</param>
public sealed record SqliteBulkColumn<T>(string Name, SqliteType? Type, Func<T, object?> Getter);

/// <summary>
/// SQLite bulk load options. Every load is all-or-nothing: it runs in its own transaction, or inside a savepoint of the
/// session transaction when one is active. Constraints and triggers always apply (rows are ordinary INSERTs).
/// </summary>
public sealed record SqliteBulkInsertOptions
{
    /// <summary>
    /// How long each statement may wait for a database lock; <see langword="null"/> uses the session timeout, then
    /// Microsoft.Data.Sqlite's default (30 s). SQLite does not time out a statement that is running.
    /// </summary>
    public TimeSpan? Timeout { get; init; }
}
