using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VelQuery.Generators.Infrastructure;

/// <summary>
/// Maps offsets in a contract's SQL value back to source spans inside its string literal, line by line, so a schema
/// diagnostic can point at the misspelled name. Works for regular, verbatim and raw (also indented multi-line) literals
/// whose lines appear verbatim in the source; a line containing escapes is not mapped and falls back to the argument.
/// </summary>
internal sealed class SqlSourceMap
{
    private readonly SyntaxTree _tree;
    private readonly int[] _valueLineStarts;
    private readonly int[] _sourceLineStarts; // Absolute source position of each value line, or -1 when unmapped.
    private readonly int[] _valueLineLengths;

    private SqlSourceMap(SyntaxTree tree, int[] valueLineStarts, int[] sourceLineStarts, int[] valueLineLengths)
    {
        _tree = tree;
        _valueLineStarts = valueLineStarts;
        _sourceLineStarts = sourceLineStarts;
        _valueLineLengths = valueLineLengths;
    }

    public static SqlSourceMap? Create(ExpressionSyntax? argument, string sql)
    {
        if (argument is not LiteralExpressionSyntax literal || literal.SyntaxTree is null)
        {
            return null;
        }

        var text = literal.Token.Text;
        var valueStarts = new List<int>();
        var sourceStarts = new List<int>();
        var lengths = new List<int>();
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
            }
            else
            {
                sourceStarts.Add(literal.Token.SpanStart + found);
                searchFrom = found + line.Length;
            }

            if (newline < 0)
            {
                break;
            }

            lineStart = newline + 1;
        }

        return new SqlSourceMap(literal.SyntaxTree, valueStarts.ToArray(), sourceStarts.ToArray(), lengths.ToArray());
    }

    /// <summary>Source location of <c>sql[start..start+length]</c>, or null when that line could not be mapped.</summary>
    public LocationInfo? Map(int start, int length)
    {
        var line = Array.BinarySearch(_valueLineStarts, start);
        if (line < 0)
        {
            line = ~line - 1;
        }

        if (line < 0 || _sourceLineStarts[line] < 0 || start + length > _valueLineStarts[line] + _valueLineLengths[line])
        {
            return null;
        }

        return LocationInfo.From(_tree, new TextSpan(_sourceLineStarts[line] + (start - _valueLineStarts[line]), length));
    }
}
