using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace VelQuery.Cli.Schema;

/// <summary>
/// Reads table and view metadata from a live database for <c>velquery schema snapshot</c> (PRD §17.1, ADR-0006).
/// Readers only query catalog views; PostgreSQL and MySQL sessions are also switched to read-only transactions and
/// SQLite is opened read-only, so a misconfigured profile cannot write. No row data is read.
/// </summary>
internal static class SchemaReaders
{
    public static readonly string[] Providers = ["sqlserver", "postgresql", "mysql", "sqlite"];

    /// <summary>Normalizes provider aliases (the same names as the <c>VelQuerySqlDialect</c> property).</summary>
    public static string? Normalize(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        "sqlserver" or "mssql" or "azuresql" => "sqlserver",
        "postgresql" or "postgres" or "npgsql" => "postgresql",
        "mysql" or "mariadb" => "mysql",
        "sqlite" => "sqlite",
        _ => null,
    };

    public static async Task<DbSchema> ReadAsync(string provider, string connectionString, IReadOnlyCollection<string> schemas, string? logicalName, CancellationToken cancellationToken)
    {
        var connection = Open(provider, connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return provider switch
            {
                "sqlserver" => await SqlServerAsync(connection, schemas, logicalName, cancellationToken).ConfigureAwait(false),
                "postgresql" => await PostgreSqlAsync(connection, schemas, logicalName, cancellationToken).ConfigureAwait(false),
                "mysql" => await MySqlAsync(connection, schemas, logicalName, cancellationToken).ConfigureAwait(false),
                _ => await SqliteAsync(connection, logicalName, cancellationToken).ConfigureAwait(false),
            };
        }
    }

    private static DbConnection Open(string provider, string connectionString) => provider switch
    {
        "sqlserver" => new SqlConnection(connectionString),
        "postgresql" => new NpgsqlConnection(connectionString),
        "mysql" => new MySqlConnection(connectionString),
        _ => new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly }.ConnectionString),
    };

    // ------------------------------------------------------------------ SQL Server

    private static async Task<DbSchema> SqlServerAsync(DbConnection connection, IReadOnlyCollection<string> schemas, string? logicalName, CancellationToken cancellationToken)
    {
        var defaultSchema = (string?)await ScalarAsync(connection, "SELECT SCHEMA_NAME()", cancellationToken).ConfigureAwait(false);
        var collation = (string?)await ScalarAsync(connection, "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))", cancellationToken).ConfigureAwait(false) ?? string.Empty;
        var caseSensitive = collation.Contains("_CS", StringComparison.OrdinalIgnoreCase) || collation.Contains("_BIN", StringComparison.OrdinalIgnoreCase);

        // TYPE_NAME(system_type_id) resolves alias types to their base type; rowversion reports as 'timestamp'. CLR
        // types (geography, geometry, hierarchyid) share system_type_id 240, which names no type: use the column's own.
        const string Sql = """
            SELECT s.name, o.name, o.type, c.name, COALESCE(TYPE_NAME(c.system_type_id), TYPE_NAME(c.user_type_id)), c.max_length, c.precision, c.scale,
                   c.is_nullable, c.is_identity, c.is_computed, CAST(CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END AS bit)
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.columns c ON c.object_id = o.object_id
            WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name, c.column_id
            """;
        var rows = new List<(string Schema, string Name, string Kind, DbColumn Column)>();
        await foreach (var r in RowsAsync(connection, Sql, cancellationToken).ConfigureAwait(false))
        {
            var schema = r.GetString(0);
            if (schemas.Count > 0 && !schemas.Contains(schema, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var type = r.GetString(4);
            var maxLength = (int)r.GetInt16(5);
            int? length = type switch
            {
                "nchar" or "nvarchar" when maxLength > 0 => maxLength / 2,
                "char" or "varchar" or "binary" or "varbinary" when maxLength > 0 => maxLength,
                _ => null,
            };
            var numeric = type is "decimal" or "numeric";
            var generated = r.GetBoolean(10) || type == "timestamp";
            rows.Add((schema, r.GetString(1), r.GetString(2).Trim() == "V" ? "view" : "table", new DbColumn(
                r.GetString(3), type, length, numeric ? r.GetByte(6) : null, numeric ? r.GetByte(7) : null,
                r.GetBoolean(8), r.GetBoolean(9) || r.GetBoolean(11) || generated, generated)));
        }

        return new DbSchema("sqlserver", logicalName, defaultSchema, caseSensitive, caseSensitive, Group(rows));
    }

    // ------------------------------------------------------------------ PostgreSQL

    private static async Task<DbSchema> PostgreSqlAsync(DbConnection connection, IReadOnlyCollection<string> schemas, string? logicalName, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, "SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY", cancellationToken).ConfigureAwait(false);
        var defaultSchema = (string?)await ScalarAsync(connection, "SELECT current_schema()", cancellationToken).ConfigureAwait(false);

        // information_schema reports view columns as nullable whether or not they can be NULL, so their
        // nullability is exported as unknown. Identity GENERATED ALWAYS and generated columns cannot be written.
        const string Sql = """
            SELECT c.table_schema, c.table_name, t.table_type, c.column_name,
                   CASE WHEN c.data_type IN ('ARRAY', 'USER-DEFINED') THEN c.udt_name ELSE c.data_type END,
                   c.character_maximum_length, c.numeric_precision, c.numeric_scale, c.is_nullable,
                   c.column_default IS NOT NULL OR c.is_identity = 'YES',
                   COALESCE(c.is_generated = 'ALWAYS' OR c.identity_generation = 'ALWAYS', false),
                   c.ordinal_position::int
            FROM information_schema.columns c
            JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema')
              AND c.table_schema NOT LIKE 'pg\_toast%' AND c.table_schema NOT LIKE 'pg\_temp%'
            UNION ALL
            -- information_schema omits materialized views; read them from the catalog (nullability unknown, no defaults).
            SELECT n.nspname, cl.relname, 'MATERIALIZED VIEW', a.attname, format_type(a.atttypid, NULL),
                   NULL::int, NULL::int, NULL::int, 'YES', false, false, a.attnum::int
            FROM pg_class cl
            JOIN pg_namespace n ON n.oid = cl.relnamespace
            JOIN pg_attribute a ON a.attrelid = cl.oid AND a.attnum > 0 AND NOT a.attisdropped
            WHERE cl.relkind = 'm' AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            ORDER BY 1, 2, 12
            """;
        var rows = new List<(string Schema, string Name, string Kind, DbColumn Column)>();
        await foreach (var r in RowsAsync(connection, Sql, cancellationToken).ConfigureAwait(false))
        {
            var schema = r.GetString(0);
            if (schemas.Count > 0 && !schemas.Contains(schema, StringComparer.Ordinal))
            {
                continue;
            }

            var view = r.GetString(2) is "VIEW" or "MATERIALIZED VIEW";
            var type = r.GetString(4);
            var numeric = type == "numeric";
            rows.Add((schema, r.GetString(1), view ? "view" : "table", new DbColumn(
                r.GetString(3), type,
                r.IsDBNull(5) ? null : r.GetInt32(5),
                numeric && !r.IsDBNull(6) ? r.GetInt32(6) : null,
                numeric && !r.IsDBNull(7) ? r.GetInt32(7) : null,
                view ? null : r.GetString(8) == "YES",
                r.GetBoolean(9) || r.GetBoolean(10),
                r.GetBoolean(10))));
        }

        return new DbSchema("postgresql", logicalName, defaultSchema, CaseSensitiveTables: true, CaseSensitiveColumns: true, Group(rows));
    }

    // ------------------------------------------------------------------ MySQL / MariaDB

    private static async Task<DbSchema> MySqlAsync(DbConnection connection, IReadOnlyCollection<string> schemas, string? logicalName, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, "SET SESSION TRANSACTION READ ONLY", cancellationToken).ConfigureAwait(false);
        var database = (string?)await ScalarAsync(connection, "SELECT DATABASE()", cancellationToken).ConfigureAwait(false);
        var lowerCase = Convert.ToInt64(await ScalarAsync(connection, "SELECT @@lower_case_table_names", cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        var targets = schemas.Count > 0 ? schemas.ToList() : database is null ? [] : [database];
        if (targets.Count == 0)
        {
            throw new InvalidOperationException("the connection string selects no database; add Database=... or pass --schema");
        }

        // COLUMN_TYPE keeps display width and signedness (tinyint(1), int unsigned), which decide the CLR type.
        var sql = $"""
            SELECT c.TABLE_SCHEMA, c.TABLE_NAME, t.TABLE_TYPE, c.COLUMN_NAME, c.COLUMN_TYPE, c.CHARACTER_MAXIMUM_LENGTH,
                   c.NUMERIC_PRECISION, c.NUMERIC_SCALE, c.IS_NULLABLE, c.COLUMN_DEFAULT, c.EXTRA
            FROM information_schema.COLUMNS c
            JOIN information_schema.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
            WHERE c.TABLE_SCHEMA IN ({string.Join(", ", targets.Select((_, i) => "@s" + i))})
            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION
            """;
        var rows = new List<(string Schema, string Name, string Kind, DbColumn Column)>();
        await foreach (var r in RowsAsync(connection, sql, cancellationToken, targets.Select((s, i) => ("@s" + i, (object)s)).ToArray()).ConfigureAwait(false))
        {
            var type = r.GetString(4);
            var extra = r.IsDBNull(10) ? string.Empty : r.GetString(10).ToUpperInvariant();
            var generated = extra.Contains("GENERATED", StringComparison.Ordinal) && !extra.Contains("DEFAULT_GENERATED", StringComparison.Ordinal)
                || extra.Contains("VIRTUAL", StringComparison.Ordinal) || extra.Contains("PERSISTENT", StringComparison.Ordinal) || extra.Contains("STORED", StringComparison.Ordinal);
            var hasDefault = !r.IsDBNull(9) || extra.Contains("AUTO_INCREMENT", StringComparison.Ordinal) || extra.Contains("DEFAULT_GENERATED", StringComparison.Ordinal) || generated;
            var numeric = type.StartsWith("decimal", StringComparison.OrdinalIgnoreCase) || type.StartsWith("numeric", StringComparison.OrdinalIgnoreCase);
            rows.Add((r.GetString(0), r.GetString(1), r.GetString(2).Contains("VIEW", StringComparison.OrdinalIgnoreCase) ? "view" : "table", new DbColumn(
                r.GetString(3), type,
                r.IsDBNull(5) ? null : (int)Math.Min(int.MaxValue, Convert.ToInt64(r.GetValue(5), System.Globalization.CultureInfo.InvariantCulture)),
                numeric && !r.IsDBNull(6) ? Convert.ToInt32(r.GetValue(6), System.Globalization.CultureInfo.InvariantCulture) : null,
                numeric && !r.IsDBNull(7) ? Convert.ToInt32(r.GetValue(7), System.Globalization.CultureInfo.InvariantCulture) : null,
                r.GetString(8) == "YES",
                hasDefault,
                generated)));
        }

        return new DbSchema("mysql", logicalName, schemas.Count == 0 ? database : null, CaseSensitiveTables: lowerCase == 0, CaseSensitiveColumns: false, Group(rows));
    }

    // ------------------------------------------------------------------ SQLite

    private static async Task<DbSchema> SqliteAsync(DbConnection connection, string? logicalName, CancellationToken cancellationToken)
    {
        // pragma_table_xinfo also lists generated columns (hidden 2 = virtual, 3 = stored).
        const string Sql = """
            SELECT m.name, m.type, p.name, p.type, p."notnull", p.dflt_value IS NOT NULL, p.pk, p.hidden,
                   (SELECT COUNT(*) FROM pragma_table_info(m.name) k WHERE k.pk > 0)
            FROM sqlite_master m
            JOIN pragma_table_xinfo(m.name) p
            WHERE m.type IN ('table', 'view') AND m.name NOT LIKE 'sqlite\_%' ESCAPE '\'
            ORDER BY m.name, p.cid
            """;
        var rows = new List<(string Schema, string Name, string Kind, DbColumn Column)>();
        await foreach (var r in RowsAsync(connection, Sql, cancellationToken).ConfigureAwait(false))
        {
            var view = r.GetString(1) == "view";
            var type = r.GetString(3);
            // A single INTEGER PRIMARY KEY column aliases the rowid: never NULL, assigned when omitted.
            var rowid = !view && r.GetInt64(6) == 1 && r.GetInt64(8) == 1 && type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase);
            var generated = r.GetInt64(7) is 2 or 3;
            rows.Add((string.Empty, r.GetString(0), view ? "view" : "table", new DbColumn(
                r.GetString(2), type, null, null, null,
                view ? null : !(rowid || r.GetInt64(4) == 1),
                r.GetBoolean(5) || rowid || generated,
                generated)));
        }

        return new DbSchema("sqlite", logicalName, null, CaseSensitiveTables: false, CaseSensitiveColumns: false, Group(rows));
    }

    // ------------------------------------------------------------------ helpers

    private static List<DbObject> Group(List<(string Schema, string Name, string Kind, DbColumn Column)> rows)
        => rows.GroupBy(r => (r.Schema, r.Name))
            .Select(g => new DbObject(g.Key.Schema.Length == 0 ? null : g.Key.Schema, g.Key.Name, g.First().Kind, g.Select(r => r.Column).ToList()))
            .ToList();

    private static async Task<object?> ScalarAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is DBNull ? null : value;
        }
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async IAsyncEnumerable<DbDataReader> RowsAsync(DbConnection connection, string sql, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    yield return reader;
                }
            }
        }
    }
}
