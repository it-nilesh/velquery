using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VelQuery.Generators.Infrastructure;

/// <summary>
/// Maps offsets in a contract's SQL value back to source spans inside its string literal, line by line, so a schema
/// diagnostic can point at the misspelled name. Works for regular, verbatim and raw (also indented multi-line) literals
/// whose lines appear verbatim in the source; a line containing escapes is not mapped and falls back to the argument.
/// It holds only values, so it can travel through the incremental pipeline and be used after the syntax tree is gone.
/// </summary>
internal sealed record SqlSourceMap(
    string FilePath,
    EquatableArray<int> ValueLineStarts,
    EquatableArray<int> ValueLineLengths,
    EquatableArray<int> SourceStarts,
    EquatableArray<int> SourceLines,
    EquatableArray<int> SourceColumns)
{
    public static SqlSourceMap? Create(ExpressionSyntax? argument, string sql)
    {
        if (argument is not LiteralExpressionSyntax literal || literal.SyntaxTree is null)
        {
            return null;
        }

        var text = literal.Token.Text;
        var tree = literal.SyntaxTree;
        var valueStarts = new List<int>();
        var lengths = new List<int>();
        var sourceStarts = new List<int>();
        var sourceLines = new List<int>();
        var sourceColumns = new List<int>();
        var searchFrom = 1; // Skip the opening quote.
        var lineStart = 0;
        while (lineStart <= sql.Length)
        {
            var newline = sql.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? sql.Length : newline;
            var line = sql.Substring(lineStart, lineEnd - lineStart);
            var found = line.Length == 0 ? searchFrom : text.IndexOf(line, searchFrom, StringComparison.Ordinal);
            valueStarts.Add(lineStart);
            lengths.Add(line.Length);
            if (found < 0)
            {
                sourceStarts.Add(-1);
                sourceLines.Add(-1);
                sourceColumns.Add(-1);
            }
            else
            {
                var absolute = literal.Token.SpanStart + found;
                var position = tree.GetLineSpan(new TextSpan(absolute, 0)).StartLinePosition;
                sourceStarts.Add(absolute);
                sourceLines.Add(position.Line);
                sourceColumns.Add(position.Character);
                searchFrom = found + line.Length;
            }

            if (newline < 0)
            {
                break;
            }

            lineStart = newline + 1;
        }

        return new SqlSourceMap(tree.FilePath, valueStarts.ToEquatableArray(), lengths.ToEquatableArray(),
            sourceStarts.ToEquatableArray(), sourceLines.ToEquatableArray(), sourceColumns.ToEquatableArray());
    }

    /// <summary>Source location of <c>sql[start..start+length]</c>, or null when that line could not be mapped.</summary>
    public LocationInfo? Map(int start, int length)
    {
        var line = -1;
        for (var i = 0; i < ValueLineStarts.Count && ValueLineStarts[i] <= start; i++)
        {
            line = i;
        }

        if (line < 0 || SourceStarts[line] < 0 || start + length > ValueLineStarts[line] + ValueLineLengths[line])
        {
            return null;
        }

        var offset = start - ValueLineStarts[line];
        var column = SourceColumns[line] + offset;
        return new LocationInfo(FilePath, new TextSpan(SourceStarts[line] + offset, length),
            new LinePositionSpan(new LinePosition(SourceLines[line], column), new LinePosition(SourceLines[line], column + length)));
    }
}
