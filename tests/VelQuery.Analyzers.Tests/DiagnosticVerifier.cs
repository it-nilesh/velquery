using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis.Text;
using VelQuery.Testing;
using Xunit;

namespace VelQuery.Analyzers.Tests;

/// <summary>
/// Verifies VelQuery diagnostics against markup: <c>{|VQ1001:text|}</c> marks the exact span where a diagnostic with
/// that ID is expected. Every reported VQ diagnostic must be marked, and every mark must be reported.
/// </summary>
internal static class DiagnosticVerifier
{
    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.ComponentModel.DataAnnotations.Schema;
        using System.Data;
        using System.Threading;
        using System.Threading.Tasks;
        using VelQuery;

        """;

    public static void Verify(string markup) => Verify(markup, null, []);

    /// <summary>Verifies with a project dialect and additional files (for example a schema snapshot).</summary>
    public static void Verify(string markup, string? dialect, params (string Path, string Text)[] additionalFiles)
    {
        var (source, expected) = Parse(Usings + markup);
        var compilation = GeneratorHarness.Compile(source);
        var actual = GeneratorHarness.GetVelQueryDiagnostics(compilation, dialect, additionalFiles, new DynamicSqlAnalyzer())
            .Where(d => d.Id.StartsWith("VQ", StringComparison.Ordinal))
            .Select(d => (d.Id, d.Location.SourceSpan, Text: Slice(source, d.Location.SourceSpan), d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();

        var expectedText = expected.Select(e => $"{e.Id} '{Slice(source, e.Span)}' @{e.Span}").Order(StringComparer.Ordinal).ToArray();
        var actualText = actual.Select(a => $"{a.Id} '{a.Text}' @{a.SourceSpan}").Order(StringComparer.Ordinal).ToArray();
        Assert.True(expectedText.SequenceEqual(actualText),
            "Expected:\n  " + string.Join("\n  ", expectedText) + "\nActual:\n  " + string.Join("\n  ", actual.Select(a => $"{a.Id} '{a.Text}' @{a.SourceSpan}: {a.Item4}")));
    }

    /// <summary>Returns the messages of reported VelQuery diagnostics (for message-content assertions).</summary>
    public static IReadOnlyList<string> Messages(string source, string? dialect = null, params (string Path, string Text)[] additionalFiles)
        => GeneratorHarness.GetVelQueryDiagnostics(GeneratorHarness.Compile(Usings + source), dialect, additionalFiles, new DynamicSqlAnalyzer())
            .Where(d => d.Id.StartsWith("VQ", StringComparison.Ordinal))
            .Select(d => d.Id + ": " + d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

    private static string Slice(string source, TextSpan span)
        => span.End <= source.Length ? source.Substring(span.Start, span.Length) : "<out of range>";

    private static (string Source, List<(string Id, TextSpan Span)> Expected) Parse(string markup)
    {
        var builder = new StringBuilder();
        var expected = new List<(string, TextSpan)>();
        var i = 0;
        while (i < markup.Length)
        {
            if (string.CompareOrdinal(markup, i, "{|", 0, 2) == 0)
            {
                var colon = markup.IndexOf(':', i);
                var id = markup.Substring(i + 2, colon - i - 2);
                var end = markup.IndexOf("|}", colon, StringComparison.Ordinal);
                var content = markup.Substring(colon + 1, end - colon - 1);
                expected.Add((id, new TextSpan(builder.Length, content.Length)));
                builder.Append(content);
                i = end + 2;
            }
            else
            {
                builder.Append(markup[i]);
                i++;
            }
        }

        return (builder.ToString(), expected);
    }
}
