namespace VelQuery.Generators.Sql;

/// <summary>SQL dialect used to scan contract SQL (VQ-DB-008). The value is an index into per-dialect parse results.</summary>
internal enum SqlDialect
{
    SqlServer = 0,
    PostgreSql = 1,
    MySql = 2,
    Sqlite = 3,
    Generic = 4,
}

internal static class SqlDialects
{
    public const int Count = 5;

    /// <summary>
    /// Parses the <c>VelQuerySqlDialect</c> MSBuild property or the attribute's <c>Dialect</c>. Empty means SQL Server
    /// (the original T-SQL rules). Returns false for unknown names, with <see cref="SqlDialect.Generic"/> as fallback.
    /// </summary>
    public static bool TryParse(string? value, out SqlDialect dialect)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "sqlserver":
            case "mssql":
            case "tsql":
            case "azuresql":
                dialect = SqlDialect.SqlServer;
                return true;
            case "postgresql":
            case "postgres":
            case "npgsql":
                dialect = SqlDialect.PostgreSql;
                return true;
            case "mysql":
            case "mariadb":
                dialect = SqlDialect.MySql;
                return true;
            case "sqlite":
                dialect = SqlDialect.Sqlite;
                return true;
            case "generic":
                dialect = SqlDialect.Generic;
                return true;
            default:
                dialect = SqlDialect.Generic;
                return false;
        }
    }

    public static string DisplayName(SqlDialect dialect) => dialect switch
    {
        SqlDialect.SqlServer => "SQL Server",
        SqlDialect.PostgreSql => "PostgreSQL",
        SqlDialect.MySql => "MySQL",
        SqlDialect.Sqlite => "SQLite",
        _ => "generic",
    };
}
