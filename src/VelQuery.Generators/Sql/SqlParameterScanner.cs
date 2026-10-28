using System;
using System.Collections.Generic;

namespace VelQuery.Generators.Sql;

/// <summary>An <c>@Name</c> placeholder found in SQL text. <see cref="Start"/> is the offset of <c>@</c>.</summary>
internal readonly record struct SqlPlaceholder(string Name, int Start, int Length);

/// <summary>Result of scanning SQL text for named placeholders.</summary>
internal sealed class SqlScanResult(IReadOnlyList<SqlPlaceholder> placeholders, string? unterminated, IReadOnlyList<SqlPlaceholder> foreign)
{
    /// <summary>Placeholders in source order (duplicates included).</summary>
    public IReadOnlyList<SqlPlaceholder> Placeholders { get; } = placeholders;

    /// <summary>The kind of construct left open at end of text (for example "string literal"), or null.</summary>
    public string? Unterminated { get; } = unterminated;

    /// <summary>
    /// Placeholders in a style VelQuery does not bind (<c>?</c>, <c>?NNN</c>, <c>:name</c>, <c>$name</c>, <c>$1</c>);
    /// <see cref="SqlPlaceholder.Name"/> holds the text as written.
    /// </summary>
    public IReadOnlyList<SqlPlaceholder> Foreign { get; } = foreign;
}

/// <summary>
/// Dialect-aware lexical scanner for named placeholders (VQ-FR-033, VQ-DB-008). It is deliberately not a parser: it only
/// skips constructs in which <c>@</c> is not a placeholder. Per dialect:
/// <list type="bullet">
/// <item>All: <c>'..'</c> strings with <c>''</c>, <c>--</c> line comments, <c>/* */</c> block comments.</item>
/// <item>SQL Server: <c>[..]</c> and <c>".."</c> identifiers, nested block comments, <c>@@</c> system functions.</item>
/// <item>PostgreSQL: <c>".."</c> identifiers, nested block comments, <c>$tag$..$tag$</c> dollar quotes,
/// <c>E'..'</c> backslash escapes, <c>::</c> casts; <c>[</c> is an array subscript, <c>@@</c>/<c>?</c> are operators.</item>
/// <item>MySQL: backslash escapes in <c>'..'</c> and <c>".."</c> strings, backtick identifiers, <c>#</c> and
/// <c>-- </c> comments, non-nesting block comments whose <c>/*! */</c> form is executed (scanned), <c>@@</c> system
/// variables, and <c>@var</c> user variables (assigned with <c>:=</c>, <c>SET</c> or <c>INTO</c>).</item>
/// <item>SQLite: <c>[..]</c>, <c>".."</c> and backtick identifiers, non-nesting block comments.</item>
/// </list>
/// </summary>
internal static class SqlParameterScanner
{
    public static SqlScanResult Scan(string sql, SqlDialect dialect = SqlDialect.SqlServer)
    {
        var placeholders = new List<SqlPlaceholder>();
        var foreign = new List<SqlPlaceholder>();
        SqlScanResult Unterminated(string what) => new(placeholders, what, foreign);

        var mySql = dialect == SqlDialect.MySql;
        var postgres = dialect == SqlDialect.PostgreSql;
        var sqlite = dialect == SqlDialect.Sqlite;
        var bracketIdentifiers = dialect is SqlDialect.SqlServer or SqlDialect.Sqlite or SqlDialect.Generic;
        var backtickIdentifiers = dialect is SqlDialect.MySql or SqlDialect.Sqlite or SqlDialect.Generic;
        var nestedComments = dialect is SqlDialect.SqlServer or SqlDialect.PostgreSql;
        var questionPlaceholders = dialect is SqlDialect.MySql or SqlDialect.Sqlite or SqlDialect.Generic;
        var colonPlaceholders = dialect is SqlDialect.MySql or SqlDialect.Sqlite or SqlDialect.Generic;

        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            switch (c)
            {
                case '\'':
                    if (!SkipQuoted(sql, ref i, '\'', backslashEscapes: mySql))
                    {
                        return Unterminated("string literal");
                    }

                    break;
                case '"':
                    if (!SkipQuoted(sql, ref i, '"', backslashEscapes: mySql))
                    {
                        return Unterminated(mySql ? "string literal" : "quoted identifier");
                    }

                    break;
                case '[' when bracketIdentifiers:
                    if (!SkipQuoted(sql, ref i, ']', backslashEscapes: false))
                    {
                        return Unterminated("bracketed identifier");
                    }

                    break;
                case '`' when backtickIdentifiers:
                    if (!SkipQuoted(sql, ref i, '`', backslashEscapes: false))
                    {
                        return Unterminated("quoted identifier");
                    }

                    break;
                case '-' when Peek(sql, i + 1) == '-' && (!mySql || IsCommentSpace(Peek(sql, i + 2))):
                case '#' when mySql:
                    SkipLine(sql, ref i);
                    break;
                case '/' when Peek(sql, i + 1) == '*':
                    if (mySql && Peek(sql, i + 2) == '!')
                    {
                        // MySQL executable comment: the server runs its content, so keep scanning it.
                        i += 3;
                        while (i < sql.Length && char.IsDigit(sql[i]))
                        {
                            i++;
                        }

                        break;
                    }

                    if (!SkipBlockComment(sql, ref i, nestedComments))
                    {
                        return Unterminated("block comment");
                    }

                    break;
                case '*' when mySql && Peek(sql, i + 1) == '/':
                    i += 2; // End of an executable comment.
                    break;
                case '$' when postgres:
                    if (TryDollarTag(sql, i, out var tag))
                    {
                        var close = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                        if (close < 0)
                        {
                            return Unterminated("dollar-quoted string");
                        }

                        i = close + tag.Length;
                    }
                    else if (char.IsDigit(Peek(sql, i + 1)))
                    {
                        AddForeign(sql, ref i, foreign, 1, char.IsDigit);
                    }
                    else
                    {
                        i++;
                    }

                    break;
                case '$' when sqlite && IsIdentifierStart(Peek(sql, i + 1)):
                    AddForeign(sql, ref i, foreign, 1, IsIdentifierPart);
                    break;
                case '?' when questionPlaceholders:
                    AddForeign(sql, ref i, foreign, 1, char.IsDigit);
                    break;
                case ':' when Peek(sql, i + 1) is ':' or '=':
                    i += 2; // PostgreSQL cast or := assignment.
                    break;
                case ':' when colonPlaceholders && IsIdentifierStart(Peek(sql, i + 1)):
                    AddForeign(sql, ref i, foreign, 1, IsIdentifierPart);
                    break;
                case '@' when Peek(sql, i + 1) == '@':
                    i += 2;
                    if (!postgres)
                    {
                        // @@ROWCOUNT, @@session.x: system functions/variables, never parameters.
                        while (i < sql.Length && (IsIdentifierPart(sql[i]) || (mySql && sql[i] == '.')))
                        {
                            i++;
                        }
                    }

                    break;
                case '@' when IsIdentifierStart(Peek(sql, i + 1)):
                    var start = i;
                    i++;
                    while (i < sql.Length && IsIdentifierPart(sql[i]))
                    {
                        i++;
                    }

                    placeholders.Add(new SqlPlaceholder(sql.Substring(start + 1, i - start - 1), start, i - start));
                    break;
                default:
                    if (IsIdentifierPart(c))
                    {
                        var wordStart = i;
                        while (i < sql.Length && IsIdentifierPart(sql[i]))
                        {
                            i++;
                        }

                        // PostgreSQL E'..' strings use backslash escapes.
                        if (postgres && i - wordStart == 1 && (c is 'E' or 'e') && Peek(sql, i) == '\'' &&
                            !SkipQuoted(sql, ref i, '\'', backslashEscapes: true))
                        {
                            return Unterminated("string literal");
                        }
                    }
                    else
                    {
                        i++;
                    }

                    break;
            }
        }

        if (dialect == SqlDialect.SqlServer)
        {
            RemoveDeclaredVariables(sql, placeholders);
        }

        if (mySql)
        {
            RemoveUserVariables(sql, placeholders);
        }

        return new SqlScanResult(placeholders, null, foreign);
    }

    /// <summary>
    /// MySQL <c>@name</c> is a session user variable when the SQL assigns it (<c>@x :=</c>, <c>SET @x =</c>,
    /// <c>INTO @x</c>); every occurrence of such a name is a variable, not a parameter.
    /// </summary>
    private static void RemoveUserVariables(string sql, List<SqlPlaceholder> placeholders)
    {
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in placeholders)
        {
            var after = SkipSpaces(sql, p.Start + p.Length);
            if (Peek(sql, after) == ':' && Peek(sql, after + 1) == '=')
            {
                assigned.Add(p.Name);
                continue;
            }

            var word = PreviousWord(sql, p.Start);
            if (word.Equals("SET", StringComparison.OrdinalIgnoreCase) || word.Equals("INTO", StringComparison.OrdinalIgnoreCase))
            {
                assigned.Add(p.Name);
            }
        }

        if (assigned.Count > 0)
        {
            placeholders.RemoveAll(p => assigned.Contains(p.Name));
        }
    }

    /// <summary>
    /// T-SQL local variables (<c>DECLARE @n int = 1, @t TABLE (...)</c>) belong to the batch, not to the caller: every
    /// occurrence of a declared name is a variable, not a parameter. A placeholder used in a declaration's initializer
    /// (<c>DECLARE @n int = @limit</c>) is still a parameter. Names compare case-insensitively, like T-SQL variables.
    /// </summary>
    private static void RemoveDeclaredVariables(string sql, List<SqlPlaceholder> placeholders)
    {
        var tokens = SqlTokenizer.Tokenize(sql, SqlDialect.SqlServer);
        if (tokens is null || placeholders.Count == 0)
        {
            return;
        }

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].IsWord("DECLARE"))
            {
                continue;
            }

            // Each declaration starts with @name; it runs to a top-level comma, or ends at ';' or the next statement.
            var expectName = true;
            var depth = 0;
            for (i++; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.IsPunctuation('('))
                {
                    depth++;
                }
                else if (token.IsPunctuation(')'))
                {
                    depth--;
                }
                else if (depth == 0 && (token.IsPunctuation(';') || (token.Kind == SqlTokenKind.Word && StatementStarts.Contains(token.Text))))
                {
                    i--; // Let the outer loop see a following DECLARE.
                    break;
                }
                else if (depth == 0 && token.IsPunctuation(','))
                {
                    expectName = true;
                    continue;
                }
                else if (expectName && token.Kind == SqlTokenKind.Parameter && !token.Text.StartsWith("@@", StringComparison.Ordinal))
                {
                    declared.Add(token.Text.Substring(1));
                }

                expectName = false;
            }
        }

        if (declared.Count > 0)
        {
            placeholders.RemoveAll(p => declared.Contains(p.Name));
        }
    }

    /// <summary>
    /// Words that start a new T-SQL statement and so end a DECLARE without a semicolon. Words that also occur inside
    /// expressions (END of CASE ... END) are deliberately absent.
    /// </summary>
    private static readonly HashSet<string> StatementStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "SET", "IF", "WHILE", "BEGIN", "EXEC", "EXECUTE",
        "DECLARE", "WITH", "RETURN", "PRINT", "OPEN", "FETCH", "CLOSE", "DEALLOCATE", "THROW", "RAISERROR", "TRUNCATE",
    };

    private static string PreviousWord(string sql, int index)
    {
        var end = index;
        while (end > 0 && char.IsWhiteSpace(sql[end - 1]))
        {
            end--;
        }

        var start = end;
        while (start > 0 && IsIdentifierPart(sql[start - 1]))
        {
            start--;
        }

        return sql.Substring(start, end - start);
    }

    private static int SkipSpaces(string sql, int index)
    {
        while (index < sql.Length && char.IsWhiteSpace(sql[index]))
        {
            index++;
        }

        return index;
    }

    private static void AddForeign(string sql, ref int i, List<SqlPlaceholder> foreign, int prefixLength, Func<char, bool> part)
    {
        var start = i;
        i += prefixLength;
        while (i < sql.Length && part(sql[i]))
        {
            i++;
        }

        foreign.Add(new SqlPlaceholder(sql.Substring(start, i - start), start, i - start));
    }

    /// <summary>A PostgreSQL dollar-quote opener: <c>$$</c> or <c>$tag$</c> (tag: identifier not starting with a digit).</summary>
    private static bool TryDollarTag(string sql, int i, out string tag)
    {
        var j = i + 1;
        if (j < sql.Length && IsIdentifierStart(sql[j]))
        {
            while (j < sql.Length && IsIdentifierPart(sql[j]))
            {
                j++;
            }
        }

        if (Peek(sql, j) == '$')
        {
            tag = sql.Substring(i, j - i + 1);
            return true;
        }

        tag = string.Empty;
        return false;
    }

    private static void SkipLine(string sql, ref int i)
    {
        while (i < sql.Length && sql[i] != '\n' && sql[i] != '\r')
        {
            i++;
        }
    }

    /// <summary>Skips a quoted run starting at <paramref name="i"/>; a doubled closing quote (and, if enabled, a backslash) escapes.</summary>
    private static bool SkipQuoted(string sql, ref int i, char close, bool backslashEscapes)
    {
        i++;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (backslashEscapes && c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == close)
            {
                if (Peek(sql, i + 1) == close)
                {
                    i += 2;
                    continue;
                }

                i++;
                return true;
            }

            i++;
        }

        return false;
    }

    private static bool SkipBlockComment(string sql, ref int i, bool nested)
    {
        var depth = 0;
        while (i < sql.Length)
        {
            if (sql[i] == '/' && Peek(sql, i + 1) == '*' && (nested || depth == 0))
            {
                depth++;
                i += 2;
            }
            else if (sql[i] == '*' && Peek(sql, i + 1) == '/')
            {
                depth--;
                i += 2;
                if (depth == 0)
                {
                    return true;
                }
            }
            else
            {
                i++;
            }
        }

        return false;
    }

    /// <summary>MySQL only treats <c>--</c> as a comment when followed by whitespace, a control character or the end.</summary>
    private static bool IsCommentSpace(char c) => c == '\0' || char.IsWhiteSpace(c) || char.IsControl(c);

    private static char Peek(string sql, int index) => index < sql.Length ? sql[index] : '\0';

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
