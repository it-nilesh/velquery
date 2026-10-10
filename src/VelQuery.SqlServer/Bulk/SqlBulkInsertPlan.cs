using System;
using System.Collections.Immutable;

namespace VelQuery.SqlServer.Bulk;

/// <summary>
/// Immutable description of a bulk insert: destination table, column getters and options. Build it once (for example
/// in a static field) and reuse it; it is thread-safe. Getters are plain delegates, so no reflection is involved.
/// </summary>
/// <example>
/// <code>
/// static readonly SqlBulkInsertPlan&lt;Product&gt; ProductsPlan = SqlBulkInsertPlan&lt;Product&gt;.Create("dbo.Products")
///     .Column("Name", p => p.Name)
///     .Column("Price", p => p.Price)
///     .Column("Stock", p => p.Stock);
/// </code>
/// </example>
public sealed class SqlBulkInsertPlan<T>
{
    private SqlBulkInsertPlan(string table, string quotedTable, ImmutableArray<BulkColumn<T>> columns, SqlBulkInsertOptions options, string queryId)
    {
        Table = table;
        QuotedTable = quotedTable;
        Columns = columns;
        Options = options;
        QueryId = queryId;
    }

    /// <summary>Destination table as supplied, for example <c>dbo.Products</c>.</summary>
    public string Table { get; }

    /// <summary>Destination table quoted by the SQL Server provider, for example <c>[dbo].[Products]</c>.</summary>
    public string QuotedTable { get; }

    /// <summary>Mapped columns in source order.</summary>
    public ImmutableArray<BulkColumn<T>> Columns { get; }

    /// <summary>Copy options.</summary>
    public SqlBulkInsertOptions Options { get; }

    /// <summary>Diagnostic identity used in errors (defaults to <c>bulk:&lt;table&gt;</c>).</summary>
    public string QueryId { get; }

    /// <summary>
    /// Starts a plan for <paramref name="table"/> (<c>table</c> or <c>schema.table</c>). Each part must be a plain
    /// identifier (letters, digits, underscore); the name is quoted, never interpolated raw (VQ-FR-042).
    /// </summary>
    public static SqlBulkInsertPlan<T> Create(string table, SqlBulkInsertOptions? options = null)
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
            quoted[i] = SqlServerProvider.Instance.QuoteIdentifier(parts[i]);
        }

        return new SqlBulkInsertPlan<T>(table, string.Join('.', quoted), [], options ?? new SqlBulkInsertOptions(), "bulk:" + table);
    }

    /// <summary>
    /// Returns a copy that also maps <paramref name="getter"/> to destination column <paramref name="column"/>.
    /// <typeparamref name="TValue"/> (inferred from the getter) types the staging column sent to the server.
    /// </summary>
    public SqlBulkInsertPlan<T> Column<TValue>(string column, Func<T, TValue> getter)
    {
        ValidateIdentifier(column, nameof(column));
        ArgumentNullException.ThrowIfNull(getter);
        foreach (var existing in Columns)
        {
            if (string.Equals(existing.Name, column, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Column '{column}' is already mapped.", nameof(column));
            }
        }

        var valueType = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
        if (valueType.IsEnum)
        {
            throw new ArgumentException($"Column '{column}': SqlBulkCopy cannot write enum values; map the underlying value instead, e.g. r => (int)r.Status.", nameof(getter));
        }

        return new SqlBulkInsertPlan<T>(Table, QuotedTable, Columns.Add(new BulkColumn<T>(column, valueType, row => getter(row))), Options, QueryId);
    }

    /// <summary>Returns a copy with different options.</summary>
    public SqlBulkInsertPlan<T> WithOptions(SqlBulkInsertOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SqlBulkInsertPlan<T>(Table, QuotedTable, Columns, options, QueryId);
    }

    /// <summary>Returns a copy with a different diagnostic identity.</summary>
    public SqlBulkInsertPlan<T> WithQueryId(string queryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryId);
        return new SqlBulkInsertPlan<T>(Table, QuotedTable, Columns, Options, queryId);
    }

    private static void ValidateIdentifier(string value, string parameter)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || char.IsDigit(value[0]))
        {
            throw new ArgumentException($"'{value}' is not a valid SQL Server identifier.", parameter);
        }

        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                throw new ArgumentException($"'{value}' is not a valid SQL Server identifier.", parameter);
            }
        }
    }
}

/// <summary>A mapped bulk column.</summary>
/// <param name="Name">Destination column name.</param>
/// <param name="ValueType">CLR type of the values (nullable types unwrapped); used for the staging column.</param>
/// <param name="Getter">Reads the value from a row; <see langword="null"/> is written as SQL NULL.</param>
public sealed record BulkColumn<T>(string Name, Type ValueType, Func<T, object?> Getter);

/// <summary>
/// Bulk insert options. Defaults match ordinary <c>INSERT</c> semantics, which raw <c>SqlBulkCopy</c> does not:
/// the load is all-or-nothing, CHECK constraints are enforced, and NULL stays NULL instead of taking column defaults.
/// </summary>
public sealed record SqlBulkInsertOptions
{
    /// <summary>Rows staged and sent per batch; also bounds client memory. Default 10,000.</summary>
    public int BatchSize { get; init; } = 10_000;

    /// <summary>
    /// When true (default) and the session has no active transaction, the whole load runs in one transaction so a
    /// failure leaves no partial data. Inside a session transaction, the load always enlists in it. Set false to
    /// commit batch by batch (less log pressure, but a failure can leave earlier batches committed).
    /// </summary>
    public bool Atomic { get; init; } = true;

    /// <summary>Enforce CHECK and FOREIGN KEY constraints. Default true.</summary>
    public bool CheckConstraints { get; init; } = true;

    /// <summary>Fire insert triggers. Default false (same as SqlBulkCopy).</summary>
    public bool FireTriggers { get; init; }

    /// <summary>Keep NULLs rather than applying column defaults. Default true (same as INSERT).</summary>
    public bool KeepNulls { get; init; } = true;

    /// <summary>
    /// Take a table lock for the duration of the load. Faster and enables minimal logging on heaps, but blocks other
    /// writers. Default false.
    /// </summary>
    public bool TableLock { get; init; }

    /// <summary>Timeout for the whole operation; <see langword="null"/> uses the session timeout, then 30 s.</summary>
    public TimeSpan? Timeout { get; init; }
}
