using System;
using System.Collections.Immutable;

namespace VelQuery.MySql.Bulk;

/// <summary>
/// Immutable description of a MySQL/MariaDB bulk load via <c>LOAD DATA LOCAL INFILE</c> (MySqlConnector's
/// <see cref="MySqlConnector.MySqlBulkCopy"/>): destination table, typed column getters and options. Build it once and
/// reuse it; it is thread-safe and uses no reflection.
/// </summary>
/// <example>
/// <code>
/// static readonly MySqlBulkInsertPlan&lt;Product&gt; Plan = MySqlBulkInsertPlan&lt;Product&gt;.Create("shop.products")
///     .Column("name", p => p.Name)
///     .Column("price", p => p.Price)
///     .Column("created_at", p => p.CreatedAt);
/// </code>
/// </example>
public sealed class MySqlBulkInsertPlan<T>
{
    private MySqlBulkInsertPlan(string table, string quotedTable, ImmutableArray<MySqlBulkColumn<T>> columns, MySqlBulkInsertOptions options, string queryId)
    {
        Table = table;
        QuotedTable = quotedTable;
        Columns = columns;
        Options = options;
        QueryId = queryId;
    }

    /// <summary>Destination table as supplied, for example <c>shop.products</c>.</summary>
    public string Table { get; }

    /// <summary>Destination table quoted by the provider, for example <c>`shop`.`products`</c>.</summary>
    public string QuotedTable { get; }

    /// <summary>Mapped columns in load order.</summary>
    public ImmutableArray<MySqlBulkColumn<T>> Columns { get; }

    /// <summary>Load options.</summary>
    public MySqlBulkInsertOptions Options { get; }

    /// <summary>Diagnostic identity used in errors (defaults to <c>bulk:&lt;table&gt;</c>).</summary>
    public string QueryId { get; }

    /// <summary>True when a column holds <see cref="Guid"/> or <see cref="byte"/>[] values, which need a destination type check.</summary>
    internal bool HasHexEncodedColumns
    {
        get
        {
            foreach (var column in Columns)
            {
                if (column.ValueType == typeof(Guid) || column.ValueType == typeof(byte[]))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Starts a plan for <paramref name="table"/> (<c>table</c> or <c>database.table</c>). Each part must be a plain
    /// identifier (letters, digits, underscore, <c>$</c>; at most 64 characters); names are quoted, never interpolated raw
    /// (VQ-FR-042).
    /// </summary>
    public static MySqlBulkInsertPlan<T> Create(string table, MySqlBulkInsertOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        var parts = table.Split('.');
        if (parts.Length > 2)
        {
            throw new ArgumentException("Use 'table' or 'database.table'.", nameof(table));
        }

        var quoted = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            ValidateIdentifier(parts[i], nameof(table));
            quoted[i] = MySqlProvider.Instance.QuoteIdentifier(parts[i]);
        }

        return new MySqlBulkInsertPlan<T>(table, string.Join('.', quoted), [], options ?? new MySqlBulkInsertOptions(), "bulk:" + table);
    }

    /// <summary>
    /// Returns a copy that also maps <paramref name="getter"/> to destination column <paramref name="column"/>.
    /// <typeparamref name="TValue"/> is inferred from the getter; <see langword="null"/> is written as SQL NULL.
    /// </summary>
    public MySqlBulkInsertPlan<T> Column<TValue>(string column, Func<T, TValue> getter)
    {
        ValidateIdentifier(column, nameof(column));
        ArgumentNullException.ThrowIfNull(getter);
        var valueType = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
        if (valueType.IsEnum)
        {
            // MySqlBulkCopy would write the enum's name or number depending on the driver path; make the choice explicit.
            throw new ArgumentException(
                $"Column '{column}': map enums explicitly, e.g. r => (int)r.Status for an integer column or r => r.Status.ToString() for a MySQL ENUM column.",
                nameof(getter));
        }

        foreach (var existing in Columns)
        {
            // MySQL column names are case-insensitive.
            if (string.Equals(existing.Name, column, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Column '{column}' is already mapped.", nameof(column));
            }
        }

        return new MySqlBulkInsertPlan<T>(Table, QuotedTable, Columns.Add(new MySqlBulkColumn<T>(column, valueType, row => getter(row))), Options, QueryId);
    }

    /// <summary>Returns a copy with different options.</summary>
    public MySqlBulkInsertPlan<T> WithOptions(MySqlBulkInsertOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new MySqlBulkInsertPlan<T>(Table, QuotedTable, Columns, options, QueryId);
    }

    /// <summary>Returns a copy with a different diagnostic identity.</summary>
    public MySqlBulkInsertPlan<T> WithQueryId(string queryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryId);
        return new MySqlBulkInsertPlan<T>(Table, QuotedTable, Columns, Options, queryId);
    }

    private static void ValidateIdentifier(string value, string parameter)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MySqlProvider.MaxIdentifierLength || char.IsDigit(value[0]))
        {
            throw new ArgumentException($"'{value}' is not a valid MySQL identifier.", parameter);
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '$')
            {
                throw new ArgumentException($"'{value}' is not a valid MySQL identifier.", parameter);
            }
        }
    }
}

/// <summary>A mapped bulk column.</summary>
/// <param name="Name">Destination column name (validated; MySqlConnector quotes it in the LOAD DATA column list).</param>
/// <param name="ValueType">CLR type of the values (nullable types unwrapped).</param>
/// <param name="Getter">Reads the value from a row; <see langword="null"/> is written as SQL NULL.</param>
public sealed record MySqlBulkColumn<T>(string Name, Type ValueType, Func<T, object?> Getter);

/// <summary>
/// MySQL bulk load options. Defaults match ordinary <c>INSERT</c> semantics, which raw <c>LOAD DATA LOCAL</c> does not:
/// MySQL treats a LOCAL load as <c>IGNORE</c> (duplicate-key, CHECK and foreign-key violations skip the row; NULL into
/// NOT NULL and over-long values are coerced with only a warning). VelQuery therefore always runs the load atomically
/// (its own transaction, or a savepoint inside the session transaction) and fails it, rolling the load back, when any
/// row was skipped or any warning was raised.
/// </summary>
public sealed record MySqlBulkInsertOptions
{
    /// <summary>
    /// Timeout for the whole load; <see langword="null"/> uses the session timeout, then MySqlConnector's default.
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> disables it.
    /// </summary>
    public TimeSpan? Timeout { get; init; }
}
