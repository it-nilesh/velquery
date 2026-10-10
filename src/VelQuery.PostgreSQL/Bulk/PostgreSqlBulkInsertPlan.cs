using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace VelQuery.PostgreSQL.Bulk;

/// <summary>
/// Immutable description of a PostgreSQL bulk load via <c>COPY ... FROM STDIN (FORMAT BINARY)</c>: destination table,
/// typed column writers and options. Build it once and reuse it; it is thread-safe and uses no reflection.
/// </summary>
/// <remarks>
/// Table and column names are quoted exactly as given, so they are case-sensitive: a table created as
/// <c>CREATE TABLE products</c> must be referenced as <c>"products"</c>, not <c>"Products"</c>.
/// </remarks>
/// <example>
/// <code>
/// static readonly PostgreSqlBulkInsertPlan&lt;Product&gt; Plan = PostgreSqlBulkInsertPlan&lt;Product&gt;.Create("public.products")
///     .Column("name", p => p.Name)
///     .Column("price", p => p.Price)
///     .Column("created_at", p => p.CreatedAt, NpgsqlDbType.TimestampTz);
/// </code>
/// </example>
public sealed class PostgreSqlBulkInsertPlan<T>
{
    private PostgreSqlBulkInsertPlan(string table, string quotedTable, ImmutableArray<PostgreSqlBulkColumn<T>> columns, PostgreSqlBulkInsertOptions options, string queryId)
    {
        Table = table;
        QuotedTable = quotedTable;
        Columns = columns;
        Options = options;
        QueryId = queryId;
    }

    /// <summary>Destination table as supplied, for example <c>public.products</c>.</summary>
    public string Table { get; }

    /// <summary>Destination table quoted by the provider, for example <c>"public"."products"</c>.</summary>
    public string QuotedTable { get; }

    /// <summary>Mapped columns in COPY order.</summary>
    public ImmutableArray<PostgreSqlBulkColumn<T>> Columns { get; }

    /// <summary>Load options.</summary>
    public PostgreSqlBulkInsertOptions Options { get; }

    /// <summary>Diagnostic identity used in errors (defaults to <c>bulk:&lt;table&gt;</c>).</summary>
    public string QueryId { get; }

    /// <summary>The COPY statement this plan executes.</summary>
    public string CopyCommand => $"COPY {QuotedTable} ({string.Join(", ", Columns.Select(c => PostgreSqlProvider.Instance.QuoteIdentifier(c.Name)))}) FROM STDIN (FORMAT BINARY)";

    /// <summary>
    /// Starts a plan for <paramref name="table"/> (<c>table</c> or <c>schema.table</c>). Each part must be a plain
    /// identifier (letters, digits, underscore); names are quoted, never interpolated raw (VQ-FR-042).
    /// </summary>
    public static PostgreSqlBulkInsertPlan<T> Create(string table, PostgreSqlBulkInsertOptions? options = null)
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
            quoted[i] = PostgreSqlProvider.Instance.QuoteIdentifier(parts[i]);
        }

        return new PostgreSqlBulkInsertPlan<T>(table, string.Join('.', quoted), [], options ?? new PostgreSqlBulkInsertOptions(), "bulk:" + table);
    }

    /// <summary>
    /// Returns a copy that also writes <paramref name="getter"/> to column <paramref name="column"/>. The value is written
    /// with its static type <typeparamref name="TValue"/> (no boxing); pass <paramref name="type"/> when the PostgreSQL
    /// column type differs from Npgsql's default mapping (for example <see cref="NpgsqlDbType.TimestampTz"/> or
    /// <see cref="NpgsqlDbType.Jsonb"/>).
    /// </summary>
    public PostgreSqlBulkInsertPlan<T> Column<TValue>(string column, Func<T, TValue> getter, NpgsqlDbType? type = null)
    {
        ValidateIdentifier(column, nameof(column));
        ArgumentNullException.ThrowIfNull(getter);
        if ((Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue)).IsEnum && type is null)
        {
            throw new ArgumentException($"Column '{column}': map enums to their underlying value (e.g. r => (int)r.Status) or pass the PostgreSQL enum type.", nameof(getter));
        }

        foreach (var existing in Columns)
        {
            if (string.Equals(existing.Name, column, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Column '{column}' is already mapped.", nameof(column));
            }
        }

        Func<NpgsqlBinaryImporter, T, CancellationToken, Task> write = type is { } npgsqlType
            ? (importer, row, ct) => getter(row) is { } value ? importer.WriteAsync(value, npgsqlType, ct) : importer.WriteNullAsync(ct)
            : (importer, row, ct) => getter(row) is { } value ? importer.WriteAsync(value, ct) : importer.WriteNullAsync(ct);
        return new PostgreSqlBulkInsertPlan<T>(Table, QuotedTable, Columns.Add(new PostgreSqlBulkColumn<T>(column, write)), Options, QueryId);
    }

    /// <summary>Returns a copy with different options.</summary>
    public PostgreSqlBulkInsertPlan<T> WithOptions(PostgreSqlBulkInsertOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new PostgreSqlBulkInsertPlan<T>(Table, QuotedTable, Columns, options, QueryId);
    }

    /// <summary>Returns a copy with a different diagnostic identity.</summary>
    public PostgreSqlBulkInsertPlan<T> WithQueryId(string queryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryId);
        return new PostgreSqlBulkInsertPlan<T>(Table, QuotedTable, Columns, Options, queryId);
    }

    private static void ValidateIdentifier(string value, string parameter)
    {
        if (string.IsNullOrEmpty(value) || value.Length > PostgreSqlProvider.MaxIdentifierLength || char.IsDigit(value[0]))
        {
            throw new ArgumentException($"'{value}' is not a valid PostgreSQL identifier.", parameter);
        }

        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                throw new ArgumentException($"'{value}' is not a valid PostgreSQL identifier.", parameter);
            }
        }
    }
}

/// <summary>A mapped COPY column.</summary>
/// <param name="Name">Destination column name (quoted as given).</param>
/// <param name="Write">Writes the row's value for this column (NULL when the getter returns null).</param>
public sealed record PostgreSqlBulkColumn<T>(string Name, Func<NpgsqlBinaryImporter, T, CancellationToken, Task> Write);

/// <summary>
/// PostgreSQL bulk load options. A COPY is a single statement, so every load is all-or-nothing, and PostgreSQL always
/// enforces constraints and fires triggers during COPY.
/// </summary>
public sealed record PostgreSqlBulkInsertOptions
{
    /// <summary>Timeout for the whole load; <see langword="null"/> uses the session timeout, then Npgsql's default.</summary>
    public TimeSpan? Timeout { get; init; }
}
