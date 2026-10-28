using System.Collections.Generic;

namespace VelQuery.Generators.Sql;

/// <summary>Lexical class of a <see cref="SqlToken"/>.</summary>
internal enum SqlTokenKind
{
    /// <summary>Unquoted identifier or keyword (also <c>#temp</c> names).</summary>
    Word,

    /// <summary>Quoted identifier; <see cref="SqlToken.Text"/> holds the unescaped name.</summary>
    QuotedIdentifier,

    /// <summary>String literal of any form (<c>'..'</c>, <c>N'..'</c>, <c>E'..'</c>, dollar-quoted, MySQL <c>".."</c>).</summary>
    String,

    Number,

    /// <summary>Placeholder or variable: <c>@name</c>, <c>@@name</c>, <c>?</c>, <c>:name</c>, <c>$1</c>.</summary>
    Parameter,

    /// <summary>One of <c>( ) , . ;</c>.</summary>
    Punctuation,

    /// <summary>Any other operator, for example <c>=</c>, <c>::</c>, <c>*</c> or <c>-&gt;&gt;</c>.</summary>
    Operator,
}

/// <summary>A SQL token with its offset and length in the SQL text.</summary>
internal readonly struct SqlToken(SqlTokenKind kind, int start, int length, string text)
{
    public SqlTokenKind Kind { get; } = kind;

    public int Start { get; } = start;

    public int Length { get; } = length;

    /// <summary>Raw text, except for quoted identifiers (unescaped content).</summary>
    public string Text { get; } = text;

    public bool IsWord(string word) => Kind == SqlTokenKind.Word && string.Equals(Text, word, System.StringComparison.OrdinalIgnoreCase);

    public bool IsPunctuation(char c) => Kind == SqlTokenKind.Punctuation && Text[0] == c;

    public bool IsOperator(string op) => Kind == SqlTokenKind.Operator && Text == op;

    /// <summary>A name: an unquoted word or a quoted identifier.</summary>
    public bool IsName => Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier;
}

/// <summary>
/// Dialect-aware SQL tokenizer for the static schema checks (ADR-0006). It follows the same quoting and comment rules
/// as <see cref="SqlParameterScanner"/>, but produces tokens instead of placeholders. Comments are dropped; MySQL
/// executable comments (<c>/*! .. */</c>) are tokenized because the server runs them. Returns null when the text
/// contains an unterminated construct (VQ1019 already reports it).
/// </summary>
internal static class SqlTokenizer
{
    public static List<SqlToken>? Tokenize(string sql, SqlDialect dialect)
    {
        var tokens = new List<SqlToken>();
        var mySql = dialect == SqlDialect.MySql;
        var postgres = dialect == SqlDialect.PostgreSql;
        var sqlServer = dialect == SqlDialect.SqlServer;
        var bracketIdentifiers = dialect is SqlDialect.SqlServer or SqlDialect.Sqlite or SqlDialect.Generic;
        var backtickIdentifiers = dialect is SqlDialect.MySql or SqlDialect.Sqlite or SqlDialect.Generic;
        var nestedComments = dialect is SqlDialect.SqlServer or SqlDialect.PostgreSql;

        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var start = i;
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            switch (c)
            {
                case '\'':
                    if (!SkipQuoted(sql, ref i, '\'', mySql))
                    {
                        return null;
                    }

                    tokens.Add(new SqlToken(SqlTokenKind.String, start, i - start, sql.Substring(start, i - start)));
                    continue;
                case '"':
                    if (!SkipQuoted(sql, ref i, '"', mySql))
                    {
                        return null;
                    }

                    tokens.Add(mySql
                        ? new SqlToken(SqlTokenKind.String, start, i - start, sql.Substring(start, i - start))
                        : new SqlToken(SqlTokenKind.QuotedIdentifier, start, i - start, Unquote(sql, start, i, '"')));
                    continue;
                case '[' when bracketIdentifiers:
                    if (!SkipQuoted(sql, ref i, ']', false))
                    {
                        return null;
                    }

                    tokens.Add(new SqlToken(SqlTokenKind.QuotedIdentifier, start, i - start, Unquote(sql, start, i, ']')));
                    continue;
                case '`' when backtickIdentifiers:
                    if (!SkipQuoted(sql, ref i, '`', false))
                    {
                        return null;
                    }

                    tokens.Add(new SqlToken(SqlTokenKind.QuotedIdentifier, start, i - start, Unquote(sql, start, i, '`')));
                    continue;
                case '-' when Peek(sql, i + 1) == '-' && (!mySql || IsCommentSpace(Peek(sql, i + 2))):
                case '#' when mySql:
                    while (i < sql.Length && sql[i] != '\n' && sql[i] != '\r')
                    {
                        i++;
                    }

                    continue;
                case '/' when Peek(sql, i + 1) == '*':
                    if (mySql && Peek(sql, i + 2) == '!')
                    {
                        i += 3;
                        while (i < sql.Length && char.IsDigit(sql[i]))
                        {
                            i++;
                        }

                        continue;
                    }

                    if (!SkipBlockComment(sql, ref i, nestedComments))
                    {
                        return null;
                    }

                    continue;
                case '*' when mySql && Peek(sql, i + 1) == '/':
                    i += 2;
                    continue;
                case '$' when postgres && TryDollarTag(sql, i, out var tag):
                    var close = sql.IndexOf(tag, i + tag.Length, System.StringComparison.Ordinal);
                    if (close < 0)
                    {
                        return null;
                    }

                    i = close + tag.Length;
                    tokens.Add(new SqlToken(SqlTokenKind.String, start, i - start, sql.Substring(start, i - start)));
                    continue;
                case '$' when char.IsDigit(Peek(sql, i + 1)) || IsIdentifierStart(Peek(sql, i + 1)):
                case ':' when IsIdentifierStart(Peek(sql, i + 1)) && Peek(sql, i - 1) != ':':
                    i++;
                    while (i < sql.Length && IsIdentifierPart(sql[i]))
                    {
                        i++;
                    }

                    tokens.Add(new SqlToken(SqlTokenKind.Parameter, start, i - start, sql.Substring(start, i - start)));
                    continue;
                case '?' when !postgres:
                    i++;
                    while (i < sql.Length && char.IsDigit(sql[i]))
                    {
                        i++;
                    }

                    tokens.Add(new SqlToken(SqlTokenKind.Parameter, start, i - start, sql.Substring(start, i - start)));
                    continue;
                case '@' when Peek(sql, i + 1) == '@' || IsIdentifierStart(Peek(sql, i + 1)):
                    i += Peek(sql, i + 1) == '@' ? 2 : 1;
                    while (i < sql.Length && (IsIdentifierPart(sql[i]) || (mySql && sql[i] == '.')))
                    {
                        i++;
                    }

                    tokens.Add(new SqlToken(SqlTokenKind.Parameter, start, i - start, sql.Substring(start, i - start)));
                    continue;
                case '(':
                case ')':
                case ',':
                case ';':
                    i++;
                    tokens.Add(new SqlToken(SqlTokenKind.Punctuation, start, 1, c.ToString()));
                    continue;
                case '.' when !char.IsDigit(Peek(sql, i + 1)):
                    i++;
                    tokens.Add(new SqlToken(SqlTokenKind.Punctuation, start, 1, "."));
                    continue;
                case '#' when sqlServer && (IsIdentifierStart(Peek(sql, i + 1)) || Peek(sql, i + 1) == '#'):
                    // Temporary table (#t, ##t): a name that never appears in a snapshot.
                    i += Peek(sql, i + 1) == '#' ? 2 : 1;
                    while (i < sql.Length && IsIdentifierPart(sql[i]))
                    {
                        i++;
                    }

                    tokens.Add(new SqlToken(SqlTokenKind.Word, start, i - start, sql.Substring(start, i - start)));
                    continue;
            }

            if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek(sql, i + 1))))
            {
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '.' || sql[i] == '_' ||
                       ((sql[i] == '+' || sql[i] == '-') && (sql[i - 1] == 'e' || sql[i - 1] == 'E'))))
                {
                    i++;
                }

                tokens.Add(new SqlToken(SqlTokenKind.Number, start, i - start, sql.Substring(start, i - start)));
                continue;
            }

            if (IsIdentifierStart(c))
            {
                i++;
                while (i < sql.Length && (IsIdentifierPart(sql[i]) || sql[i] == '$'))
                {
                    i++;
                }

                // Prefixed string literals: N'..', E'..', X'..', B'..', _utf8mb4'..'.
                if (Peek(sql, i) == '\'' && IsStringPrefix(sql.Substring(start, i - start)))
                {
                    if (!SkipQuoted(sql, ref i, '\'', mySql || sql[start] is 'E' or 'e'))
                    {
                        return null;
                    }

                    tokens.Add(new SqlToken(SqlTokenKind.String, start, i - start, sql.Substring(start, i - start)));
                    continue;
                }

                tokens.Add(new SqlToken(SqlTokenKind.Word, start, i - start, sql.Substring(start, i - start)));
                continue;
            }

            // Operators: greedily join operator characters so '::', '->>', '<>', '>=' stay one token.
            i++;
            while (i < sql.Length && IsOperatorChar(sql[i]) && !(sql[i] == '-' && Peek(sql, i + 1) == '-') && !(sql[i] == '/' && Peek(sql, i + 1) == '*'))
            {
                i++;
            }

            tokens.Add(new SqlToken(SqlTokenKind.Operator, start, i - start, sql.Substring(start, i - start)));
        }

        return tokens;
    }

    private static bool IsStringPrefix(string word)
        => word.Length == 1 ? word[0] is 'N' or 'n' or 'E' or 'e' or 'X' or 'x' or 'B' or 'b' : word[0] == '_';

    private static bool IsOperatorChar(char c) => c is '=' or '<' or '>' or '!' or '|' or '&' or '^' or '~' or '+' or '-' or '*' or '/' or '%' or ':' or '#' or '?' or '@';

    private static string Unquote(string sql, int start, int end, char close)
        => sql.Substring(start + 1, end - start - 2).Replace(new string(close, 2), close.ToString());

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

    private static bool IsCommentSpace(char c) => c == '\0' || char.IsWhiteSpace(c) || char.IsControl(c);

    private static char Peek(string sql, int index) => index >= 0 && index < sql.Length ? sql[index] : '\0';

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
