using System;
using System.Linq;
using VelQuery.MongoDB.Generators.Analysis;
using VelQuery.MongoDB.Generators.Json;
using Xunit;

namespace VelQuery.MongoDB.Generators.Tests;

/// <summary>The build-time JSON parser and source map must never throw, whatever the input (PRD §11.1).</summary>
public sealed class JsonParserTests
{
    private const string Complex = """
        [ { "$match": { "a": @x, "b": { "$in": [ 1, -2.5e3, "sé\"", true, false, null ] } } },
          { "$project": { "c": { "$literal": @y } } } ]
        """;

    [Fact]
    public void Every_prefix_and_corruption_of_valid_json_parses_or_reports_without_throwing()
    {
        Assert.Null(JsonParser.Parse(Complex).Error);
        for (var length = 0; length < Complex.Length; length++)
        {
            var (node, error) = JsonParser.Parse(Complex.Substring(0, length));
            Assert.True(node is null ^ error is null);
        }

        var random = new Random(1234);
        const string alphabet = "{}[]\",:@$\\ abc019-.eE\n";
        for (var i = 0; i < 2_000; i++)
        {
            var chars = Complex.ToCharArray();
            for (var k = 0; k < 3; k++)
            {
                chars[random.Next(chars.Length)] = alphabet[random.Next(alphabet.Length)];
            }

            var (node, error) = JsonParser.Parse(new string(chars));
            Assert.True(node is null ^ error is null);
        }
    }

    [Fact]
    public void Placeholders_in_key_position_are_flagged_specifically()
    {
        var (_, error) = JsonParser.Parse("""{ "a": { @op: 1 } }""");
        Assert.True(error!.InKeyPosition);
        Assert.Equal(9, error.Position);
    }

    [Theory]
    [InlineData("\"{ \\\"a\\\": @x }\"", "{ \"a\": @x }")]
    [InlineData("@\"{ \"\"a\"\": @x }\"", "{ \"a\": @x }")]
    [InlineData("\"\"\"{ \"a\": @x }\"\"\"", "{ \"a\": @x }")]
    [InlineData("\"\"\"\n    { \"a\":\n      @x }\n    \"\"\"", "{ \"a\":\n  @x }")]
    public void Source_map_points_at_the_placeholder_in_every_literal_form(string literal, string value)
    {
        var map = SourceMap.Build(literal, value);
        Assert.NotNull(map);
        var at = value.IndexOf('@', StringComparison.Ordinal);
        Assert.Equal("@x", literal.Substring(map[at], map[at + 2] - map[at]));
        Assert.Equal(value.Length + 1, map.Length);
        Assert.True(map.Zip(map.Skip(1)).All(p => p.First <= p.Second));
    }
}
