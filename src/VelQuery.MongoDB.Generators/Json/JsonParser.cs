using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VelQuery.MongoDB.Generators.Json;

/// <summary>A parsed JSON value with its character span in the contract string.</summary>
internal abstract class JsonNode
{
    protected JsonNode(int start, int end)
    {
        Start = start;
        End = end;
    }

    public int Start { get; }

    public int End { get; }
}

internal sealed class JsonObject(int start, int end, List<JsonProperty> properties) : JsonNode(start, end)
{
    public List<JsonProperty> Properties { get; } = properties;
}

internal sealed class JsonProperty(string name, int nameStart, int nameEnd, JsonNode value)
{
    public string Name { get; } = name;

    public int NameStart { get; } = nameStart;

    public int NameEnd { get; } = nameEnd;

    public JsonNode Value { get; } = value;
}

internal sealed class JsonArray(int start, int end, List<JsonNode> items) : JsonNode(start, end)
{
    public List<JsonNode> Items { get; } = items;
}

internal sealed class JsonString(int start, int end, string value) : JsonNode(start, end)
{
    public string Value { get; } = value;
}

/// <summary>A number lexeme exactly as written (validated against the JSON grammar).</summary>
internal sealed class JsonNumber(int start, int end, string lexeme) : JsonNode(start, end)
{
    public string Lexeme { get; } = lexeme;

    public bool IsInteger => Lexeme.IndexOfAny(['.', 'e', 'E']) < 0;
}

internal sealed class JsonLiteral(int start, int end, bool? value) : JsonNode(start, end)
{
    /// <summary>true, false, or null for the JSON null literal.</summary>
    public bool? Value { get; } = value;
}

/// <summary>An <c>@name</c> placeholder in value position.</summary>
internal sealed class JsonPlaceholder(int start, int end, string name) : JsonNode(start, end)
{
    public string Name { get; } = name;
}

/// <summary>A parse failure. <see cref="InKeyPosition"/> marks the dedicated "placeholder as key" error (VQ2006).</summary>
internal sealed class JsonError(int position, int length, string message, bool inKeyPosition = false)
{
    public int Position { get; } = position;

    public int Length { get; } = length;

    public string Message { get; } = message;

    public bool InKeyPosition { get; } = inKeyPosition;
}

/// <summary>
/// Strict JSON (RFC 8259) parser extended with <c>@name</c> placeholders in value positions, for MongoDB contract
/// strings. Runs inside the compiler, so it never throws on malformed input: the first error is returned instead.
/// Nesting depth is bounded so hostile input cannot overflow the compiler's stack.
/// </summary>
internal sealed class JsonParser
{
    private const int MaxDepth = 64;

    private readonly string _text;
    private int _position;
    private JsonError? _error;

    private JsonParser(string text) => _text = text;

    public static (JsonNode? Node, JsonError? Error) Parse(string text)
    {
        var parser = new JsonParser(text);
        parser.SkipWhitespace();
        var node = parser.ParseValue(0);
        if (parser._error is null)
        {
            parser.SkipWhitespace();
            if (parser._position < text.Length)
            {
                parser.Fail(parser._position, 1, "unexpected text after the end of the JSON value");
            }
        }

        return parser._error is null ? (node, null) : (null, parser._error);
    }

    private JsonNode? ParseValue(int depth)
    {
        if (_error is not null)
        {
            return null;
        }

        if (depth > MaxDepth)
        {
            return Fail(_position, 1, $"nesting is deeper than {MaxDepth} levels");
        }

        if (_position >= _text.Length)
        {
            return Fail(_text.Length, 0, "unexpected end of JSON; a value was expected");
        }

        var c = _text[_position];
        switch (c)
        {
            case '{':
                return ParseObject(depth);
            case '[':
                return ParseArray(depth);
            case '"':
                var start = _position;
                var value = ParseString();
                return value is null ? null : new JsonString(start, _position, value);
            case '@':
                return ParsePlaceholder();
            case 't':
                return ParseKeyword("true", true);
            case 'f':
                return ParseKeyword("false", false);
            case 'n':
                return ParseKeyword("null", null);
            default:
                if (c == '-' || (c >= '0' && c <= '9'))
                {
                    return ParseNumber();
                }

                return Fail(_position, 1, c == '\''
                    ? "single-quoted strings are not JSON; use double quotes"
                    : $"unexpected character '{Printable(c)}'; a JSON value or @placeholder was expected");
        }
    }

    private JsonNode? ParseObject(int depth)
    {
        var start = _position++;
        var properties = new List<JsonProperty>();
        SkipWhitespace();
        if (Peek() == '}')
        {
            _position++;
            return new JsonObject(start, _position, properties);
        }

        while (true)
        {
            SkipWhitespace();
            var nameStart = _position;
            var c = Peek();
            if (c == '@')
            {
                var name = ReadIdentifier(_position + 1);
                return Fail(nameStart, 1 + name.Length,
                    $"placeholder '@{name}' is used as a key or operator; placeholders may appear only as values", inKeyPosition: true);
            }

            if (c != '"')
            {
                return Fail(_position, Math.Max(1, ReadIdentifier(_position).Length), c == '\0'
                    ? "unexpected end of JSON inside an object"
                    : "expected a double-quoted property name");
            }

            var propertyName = ParseString();
            if (propertyName is null)
            {
                return null;
            }

            var nameEnd = _position;
            SkipWhitespace();
            if (Peek() != ':')
            {
                return Fail(_position, 1, "expected ':' after the property name");
            }

            _position++;
            SkipWhitespace();
            var value = ParseValue(depth + 1);
            if (value is null)
            {
                return null;
            }

            properties.Add(new JsonProperty(propertyName, nameStart, nameEnd, value));
            SkipWhitespace();
            c = Peek();
            if (c == ',')
            {
                _position++;
                continue;
            }

            if (c == '}')
            {
                _position++;
                return new JsonObject(start, _position, properties);
            }

            return Fail(_position, 1, c == '\0' ? "unexpected end of JSON; '}' was expected" : "expected ',' or '}' in object");
        }
    }

    private JsonNode? ParseArray(int depth)
    {
        var start = _position++;
        var items = new List<JsonNode>();
        SkipWhitespace();
        if (Peek() == ']')
        {
            _position++;
            return new JsonArray(start, _position, items);
        }

        while (true)
        {
            SkipWhitespace();
            var item = ParseValue(depth + 1);
            if (item is null)
            {
                return null;
            }

            items.Add(item);
            SkipWhitespace();
            var c = Peek();
            if (c == ',')
            {
                _position++;
                continue;
            }

            if (c == ']')
            {
                _position++;
                return new JsonArray(start, _position, items);
            }

            return Fail(_position, 1, c == '\0' ? "unexpected end of JSON; ']' was expected" : "expected ',' or ']' in array");
        }
    }

    private string? ParseString()
    {
        var start = _position++;
        var builder = new StringBuilder();
        while (true)
        {
            if (_position >= _text.Length)
            {
                Fail(start, _text.Length - start, "unterminated string");
                return null;
            }

            var c = _text[_position++];
            if (c == '"')
            {
                return builder.ToString();
            }

            if (c < ' ')
            {
                Fail(_position - 1, 1, "control characters must be escaped inside JSON strings");
                return null;
            }

            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }

            if (_position >= _text.Length)
            {
                Fail(start, _text.Length - start, "unterminated string");
                return null;
            }

            var escape = _text[_position++];
            switch (escape)
            {
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                case '/': builder.Append('/'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'u':
                    if (_position + 4 > _text.Length
                        || !int.TryParse(_text.Substring(_position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                    {
                        Fail(_position - 2, 2, "invalid \\u escape; four hexadecimal digits are required");
                        return null;
                    }

                    builder.Append((char)code);
                    _position += 4;
                    break;
                default:
                    Fail(_position - 2, 2, $"invalid escape sequence '\\{Printable(escape)}'");
                    return null;
            }
        }
    }

    private JsonNode? ParseNumber()
    {
        var start = _position;
        if (Peek() == '-')
        {
            _position++;
        }

        if (Peek() == '0')
        {
            _position++;
        }
        else if (IsDigit(Peek()))
        {
            while (IsDigit(Peek()))
            {
                _position++;
            }
        }
        else
        {
            return Fail(start, Math.Max(1, _position - start), "invalid number");
        }

        if (Peek() == '.')
        {
            _position++;
            if (!IsDigit(Peek()))
            {
                return Fail(start, _position - start, "invalid number; digits are required after '.'");
            }

            while (IsDigit(Peek()))
            {
                _position++;
            }
        }

        if (Peek() is 'e' or 'E')
        {
            _position++;
            if (Peek() is '+' or '-')
            {
                _position++;
            }

            if (!IsDigit(Peek()))
            {
                return Fail(start, _position - start, "invalid number; digits are required in the exponent");
            }

            while (IsDigit(Peek()))
            {
                _position++;
            }
        }

        if (IsIdentifierPart(Peek()))
        {
            return Fail(start, _position - start + 1, "invalid number");
        }

        return new JsonNumber(start, _position, _text.Substring(start, _position - start));
    }

    private JsonNode? ParsePlaceholder()
    {
        var start = _position;
        var name = ReadIdentifier(start + 1);
        if (name.Length == 0 || !IsIdentifierStart(name[0]))
        {
            return Fail(start, 1, "'@' must be followed by a parameter name (letters, digits and '_', not starting with a digit)");
        }

        _position = start + 1 + name.Length;
        return new JsonPlaceholder(start, _position, name);
    }

    private JsonNode? ParseKeyword(string keyword, bool? value)
    {
        var start = _position;
        if (string.CompareOrdinal(_text, _position, keyword, 0, keyword.Length) != 0 || IsIdentifierPart(PeekAt(_position + keyword.Length)))
        {
            var word = ReadIdentifier(_position);
            return Fail(start, Math.Max(1, word.Length), $"unexpected token '{word}'; JSON keywords are true, false and null");
        }

        _position += keyword.Length;
        return new JsonLiteral(start, _position, value);
    }

    private string ReadIdentifier(int from)
    {
        var end = from;
        while (end < _text.Length && (IsIdentifierPart(_text[end]) || _text[end] == '$'))
        {
            end++;
        }

        return _text.Substring(from, end - from);
    }

    private void SkipWhitespace()
    {
        while (_position < _text.Length && _text[_position] is ' ' or '\t' or '\n' or '\r')
        {
            _position++;
        }
    }

    private char Peek() => PeekAt(_position);

    private char PeekAt(int index) => index < _text.Length ? _text[index] : '\0';

    private JsonNode? Fail(int position, int length, string message, bool inKeyPosition = false)
    {
        _error ??= new JsonError(position, length, message, inKeyPosition);
        return null;
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private static bool IsIdentifierStart(char c) => c == '_' || char.IsLetter(c);

    private static bool IsIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);

    private static string Printable(char c) => c < ' ' ? "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture) : c.ToString();
}
