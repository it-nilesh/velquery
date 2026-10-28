using System;
using System.Collections.Generic;

namespace VelQuery.Generators.Schema;

/// <summary>Word lists for the shape builder. Being incomplete only ever makes a check skip, never fire.</summary>
internal static class SqlKeywords
{
    /// <summary>Words that are never column references inside expressions.</summary>
    public static readonly HashSet<string> Expression = Set(
        "ALL", "AND", "ANY", "ARRAY", "AS", "ASC", "ASYMMETRIC", "AT", "BETWEEN", "BINARY", "BOOLEAN", "BOTH", "BY", "CASE",
        "CENTURY", "COLLATE", "CROSS", "CUBE", "CURRENT", "CURRENT_CATALOG", "CURRENT_DATE", "CURRENT_ROLE", "CURRENT_SCHEMA",
        "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER", "DATE", "DAY", "DAY_HOUR", "DAY_MICROSECOND", "DAY_MINUTE",
        "DAY_SECOND", "DECADE", "DEFAULT", "DESC", "DISTINCT", "DIV", "DOW", "DOY", "ELSE", "END", "EPOCH", "ESCAPE", "EXCEPT",
        "EXCLUDE", "EXISTS", "EXPANSION", "FALSE", "FETCH", "FILTER", "FIRST", "FOLLOWING", "FOR", "FROM", "FULL", "GROUP",
        "GROUPING", "GROUPS", "HAVING", "HOUR", "HOUR_MICROSECOND", "HOUR_MINUTE", "HOUR_SECOND", "IGNORE", "ILIKE", "IN",
        "INNER", "INTERSECT", "INTERVAL", "IS", "ISNULL", "ISODOW", "ISOYEAR", "JOIN", "LANGUAGE", "LAST", "LEADING", "LEFT",
        "LIKE", "LIMIT", "LOCALTIME", "LOCALTIMESTAMP", "MICROSECOND", "MICROSECONDS", "MILLENNIUM", "MILLISECOND",
        "MILLISECONDS", "MINUS", "MINUTE", "MINUTE_MICROSECOND", "MINUTE_SECOND", "MOD", "MODE", "MONTH", "NATURAL", "NEXT",
        "NO", "NOT", "NOTNULL", "NULL", "NULLS", "OFFSET", "ON", "ONLY", "OR", "ORDER", "OTHERS", "OUTER", "OVER", "OVERLAPS",
        "PARTITION", "PERCENT", "PRECEDING", "PRECISION", "QUARTER", "QUERY", "RANGE", "RECURSIVE", "REGEXP", "RESPECT", "RIGHT",
        "RLIKE", "ROLLUP", "ROW", "ROWS", "SECOND", "SECOND_MICROSECOND", "SELECT", "SEPARATOR", "SESSION_USER", "SETS",
        "SIMILAR", "SOME", "SYMMETRIC", "SYSTEM_USER", "THEN", "TIES", "TIME", "TIMESTAMP", "TIMEZONE", "TIMEZONE_HOUR",
        "TIMEZONE_MINUTE", "TO", "TOP", "TRAILING", "TRUE", "UNBOUNDED", "UNION", "UNKNOWN", "USER", "USING", "VALUE", "VARYING",
        "WEEK", "WHEN", "WHERE", "WITH", "WITHIN", "WITHOUT", "XOR", "YEAR", "YEAR_MONTH", "ZONE");

    /// <summary>Words that may continue a type name after <c>::</c> (<c>double precision</c>, <c>timestamp with time zone</c>).</summary>
    public static readonly HashSet<string> TypeContinuation = Set("PRECISION", "VARYING", "WITH", "WITHOUT", "TIME", "ZONE", "LOCAL");

    /// <summary>Words that end a table factor instead of being its alias.</summary>
    public static readonly HashSet<string> NotAlias = Set(
        "APPLY", "AS", "CROSS", "DEFAULT", "EXCEPT", "FETCH", "FOR", "FORCE", "FROM", "FULL", "GROUP", "HAVING", "IGNORE", "INNER",
        "INTERSECT", "INTO", "JOIN", "LATERAL", "LEFT", "LIMIT", "LOCK", "MINUS", "NATURAL", "OFFSET", "ON", "ONLY", "OPTION",
        "ORDER", "OUTER", "OUTPUT", "OVERRIDING", "PARTITION", "QUALIFY", "RETURNING", "RIGHT", "SELECT", "SET", "STRAIGHT_JOIN",
        "TABLESAMPLE", "UNION", "USE", "USING", "VALUES", "WHEN", "WHERE", "WINDOW", "WITH");

    /// <summary>Clauses that end a SELECT list or FROM clause.</summary>
    public static readonly HashSet<string> SelectClauses = Set(
        "FROM", "WHERE", "GROUP", "HAVING", "WINDOW", "QUALIFY", "ORDER", "LIMIT", "OFFSET", "FETCH", "FOR", "INTO", "OPTION", "LOCK");

    /// <summary>Set operators separating query blocks.</summary>
    public static readonly HashSet<string> SetOperators = Set("UNION", "INTERSECT", "EXCEPT", "MINUS");

    /// <summary>Words that start a join (when not followed by an opening parenthesis, as in <c>LEFT(x, 2)</c>).</summary>
    public static readonly HashSet<string> JoinStarts = Set("JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "NATURAL", "STRAIGHT_JOIN", "OUTER");

    /// <summary>SELECT modifiers between SELECT and the select list.</summary>
    public static readonly HashSet<string> SelectModifiers = Set(
        "ALL", "DISTINCTROW", "HIGH_PRIORITY", "STRAIGHT_JOIN", "SQL_SMALL_RESULT", "SQL_BIG_RESULT", "SQL_BUFFER_RESULT",
        "SQL_NO_CACHE", "SQL_CACHE", "SQL_CALC_FOUND_ROWS");

    /// <summary>Statements that never reference tables and are skipped silently.</summary>
    public static readonly HashSet<string> Ignorable = Set("SET", "DECLARE", "BEGIN", "COMMIT", "ROLLBACK", "PRINT", "USE", "SAVEPOINT", "RELEASE");

    /// <summary>Functions whose first argument is a date part keyword, not a column.</summary>
    public static readonly HashSet<string> DatePartFirst = Set(
        "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATEPART", "DATENAME", "DATETRUNC", "DATE_BUCKET", "TIMESTAMPDIFF", "TIMESTAMPADD", "EXTRACT");

    private static HashSet<string> Set(params string[] words) => new(words, StringComparer.OrdinalIgnoreCase);
}
