using System.Collections.Generic;

namespace VelQuery.Generators.Sql;

/// <summary>An <c>@Name</c> placeholder found in SQL text. <see cref="Start"/> is the offset of <c>@</c>.</summary>
internal readonly record struct SqlPlaceholder(string Name, int Start, int Length);

/// <summary>Result of scanning SQL text for named placeholders.</summary>
internal sealed class SqlScanResult(IReadOnlyList<SqlPlaceholder> placeholders, string? unterminated)
{
    /// <summary>Placeholders in source order (duplicates included).</summary>
    public IReadOnlyList<SqlPlaceholder> Placeholders { get; } = placeholders;

    /// <summary>The kind of construct left open at end of text (for example "string literal"), or null.</summary>
    public string? Unterminated { get; } = unterminated;
}

/// <summary>
/// Lexical scanner for T-SQL style named placeholders (VQ-FR-033). It is deliberately not a parser: it only skips
/// constructs in which <c>@</c> is not a placeholder: string literals (<c>'..'</c> with <c>''</c> escapes, which also
/// covers <c>N'..'</c>), quoted identifiers (<c>[..]</c> with <c>]]</c>, <c>".."</c> with <c>""</c>), line comments
/// (<c>--</c>), nested block comments (<c>/* */</c>) and <c>@@</c> system variables.
/// </summary>
internal static class SqlParameterScanner
{
    public static SqlScanResult Scan(string sql)
    {
        var placeholders = new List<SqlPlaceholder>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            switch (c)
            {
                case '\'':
                    if (!SkipQuoted(sql, ref i, '\''))
                    {
                        return new SqlScanResult(placeholders, "string literal");
                    }

                    break;
                case '"':
                    if (!SkipQuoted(sql, ref i, '"'))
                    {
                        return new SqlScanResult(placeholders, "quoted identifier");
                    }

                    break;
                case '[':
                    if (!SkipQuoted(sql, ref i, ']'))
                    {
                        return new SqlScanResult(placeholders, "bracketed identifier");
                    }

                    break;
                case '-' when Peek(sql, i + 1) == '-':
                    while (i < sql.Length && sql[i] != '\n' && sql[i] != '\r')
                    {
                        i++;
                    }

                    break;
                case '/' when Peek(sql, i + 1) == '*':
                    if (!SkipBlockComment(sql, ref i))
                    {
                        return new SqlScanResult(placeholders, "block comment");
                    }

                    break;
                case '@' when Peek(sql, i + 1) == '@':
                    // @@ROWCOUNT and friends are system functions, never parameters.
                    i += 2;
                    while (i < sql.Length && IsIdentifierPart(sql[i]))
                    {
                        i++;
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
                        // Consume whole words (keywords, names, the N of N'..') in one step.
                        while (i < sql.Length && IsIdentifierPart(sql[i]))
                        {
                            i++;
                        }
                    }
                    else
                    {
                        i++;
                    }

                    break;
            }
        }

        return new SqlScanResult(placeholders, null);
    }

    /// <summary>Skips a quoted run starting at <paramref name="i"/>; a doubled closing quote is an escape.</summary>
    private static bool SkipQuoted(string sql, ref int i, char close)
    {
        i++;
        while (i < sql.Length)
        {
            if (sql[i] == close)
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

    private static bool SkipBlockComment(string sql, ref int i)
    {
        // T-SQL block comments nest.
        var depth = 0;
        while (i < sql.Length)
        {
            if (sql[i] == '/' && Peek(sql, i + 1) == '*')
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

    private static char Peek(string sql, int index) => index < sql.Length ? sql[index] : '\0';

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
