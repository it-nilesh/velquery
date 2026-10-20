using System;
using System.Globalization;

namespace VelQuery.MongoDB.Generators.Analysis;

/// <summary>
/// Maps each character of a string literal's value back to its offset in the literal's source text, so diagnostics
/// can point at the offending placeholder or key. Returns null for shapes it cannot map exactly; callers then fall back
/// to the whole argument.
/// </summary>
internal static class SourceMap
{
    /// <summary>
    /// An array of <c>value.Length + 1</c> offsets into <paramref name="text"/>: entry <c>i</c> is where value character
    /// <c>i</c> starts; the last entry is where the content ends.
    /// </summary>
    public static int[]? Build(string text, string value)
    {
        try
        {
            if (text.StartsWith("\"\"\"", StringComparison.Ordinal))
            {
                return Raw(text, value);
            }

            if (text.StartsWith("@\"", StringComparison.Ordinal))
            {
                return Verbatim(text, value);
            }

            if (text.StartsWith("\"", StringComparison.Ordinal))
            {
                return Regular(text, value);
            }

            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static int[]? Regular(string text, string value)
    {
        var map = new int[value.Length + 1];
        var t = 1;
        for (var v = 0; v < value.Length; v++)
        {
            map[v] = t;
            if (text[t] != '\\')
            {
                if (text[t] != value[v])
                {
                    return null;
                }

                t++;
                continue;
            }

            switch (text[t + 1])
            {
                case 'u':
                    t += 6;
                    break;
                case 'U':
                    // \U escapes above the BMP produce two UTF-16 units from one escape.
                    var code = int.Parse(text.Substring(t + 2, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                    if (code > 0xFFFF)
                    {
                        map[++v] = t;
                    }

                    t += 10;
                    break;
                case 'x':
                    var digits = 0;
                    while (digits < 4 && Uri.IsHexDigit(text[t + 2 + digits]))
                    {
                        digits++;
                    }

                    t += 2 + digits;
                    break;
                default:
                    t += 2;
                    break;
            }
        }

        map[value.Length] = t;
        return text[t] == '"' ? map : null;
    }

    private static int[]? Verbatim(string text, string value)
    {
        var map = new int[value.Length + 1];
        var t = 2;
        for (var v = 0; v < value.Length; v++)
        {
            map[v] = t;
            if (text[t] == '"')
            {
                if (text[t + 1] != '"' || value[v] != '"')
                {
                    return null;
                }

                t += 2;
            }
            else
            {
                if (text[t] != value[v])
                {
                    return null;
                }

                t++;
            }
        }

        map[value.Length] = t;
        return map;
    }

    private static int[]? Raw(string text, string value)
    {
        var quotes = 0;
        while (quotes < text.Length && text[quotes] == '"')
        {
            quotes++;
        }

        var contentEnd = text.Length - quotes;
        var firstNewline = text.IndexOf('\n', quotes);
        if (firstNewline < 0 || firstNewline >= contentEnd || text.Substring(quotes, firstNewline - quotes).Trim().Length != 0)
        {
            // Single-line raw string: the value is the text between the quotes, unchanged.
            return MapVerbatimRun(text, quotes, contentEnd, value);
        }

        // Multi-line: the closing line's whitespace is removed from every content line; the newline before the closing
        // line is not part of the value.
        var closingLineStart = text.LastIndexOf('\n', contentEnd - 1) + 1;
        var indent = contentEnd - closingLineStart;
        var map = new int[value.Length + 1];
        var v = 0;
        var t = firstNewline + 1;
        while (t < closingLineStart)
        {
            var lineEnd = text.IndexOf('\n', t);
            var skip = 0;
            while (skip < indent && t + skip < lineEnd && (text[t + skip] == ' ' || text[t + skip] == '\t'))
            {
                skip++;
            }

            t += skip;
            var contentStop = lineEnd + 1 == closingLineStart ? (lineEnd > 0 && text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd) : lineEnd + 1;
            for (; t < contentStop; t++)
            {
                if (v >= value.Length || text[t] != value[v])
                {
                    return null;
                }

                map[v++] = t;
            }

            t = lineEnd + 1;
        }

        if (v != value.Length)
        {
            return null;
        }

        map[value.Length] = closingLineStart;
        return map;
    }

    private static int[]? MapVerbatimRun(string text, int start, int end, string value)
    {
        if (end - start != value.Length || string.CompareOrdinal(text, start, value, 0, value.Length) != 0)
        {
            return null;
        }

        var map = new int[value.Length + 1];
        for (var i = 0; i <= value.Length; i++)
        {
            map[i] = start + i;
        }

        return map;
    }
}
