using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using VelQuery.Generators.Infrastructure;
using VelQuery.MongoDB.Generators.Diagnostics;
using VelQuery.MongoDB.Generators.Json;
using VelQuery.MongoDB.Generators.Model;

namespace VelQuery.MongoDB.Generators.Analysis;

/// <summary>A contract parameter that placeholders may bind.</summary>
internal sealed class BindableParameter(IParameterSymbol symbol, string field, ValueShape? shape, string? reason)
{
    public IParameterSymbol Symbol { get; } = symbol;

    public string Field { get; } = field;

    public ValueShape? Shape { get; } = shape;

    public string? Reason { get; } = reason;

    public bool Used { get; set; }

    public bool Reported { get; set; }
}

/// <summary>
/// Turns contract JSON into C# that builds the BSON directly (no JSON parsing at run time) and enforces where
/// arguments may appear. This is the NoSQL-injection guarantee (ADR-0017):
/// <list type="number">
/// <item>Placeholders are values only; a placeholder in key position is a parse error (VQ2006).</item>
/// <item>Parameters bind only as scalars or sequences of scalars, never documents (VQ2008), so no argument can add an
/// operator such as <c>$where</c> or <c>$ne</c>.</item>
/// <item>Where the server evaluates values as aggregation expressions (<c>$expr</c>, <c>$project</c>, <c>$group</c>,
/// …), arguments are wrapped in <c>$literal</c>, so a string like <c>"$password"</c> stays a string.</item>
/// <item>Positions whose values are code, identifiers or query shape (<c>$where</c>, <c>$function</c>,
/// <c>$accumulator</c>, <c>$rename</c>, <c>$getField</c>, <c>$lookup</c>, sort and projection documents, unknown
/// stages) reject placeholders (VQ2007).</item>
/// </list>
/// </summary>
internal sealed class BsonEmitter
{
    private const string Bson = "global::MongoDB.Bson.";
    private const string Values = "global::VelQuery.MongoDB.Serialization.MongoBsonValues";

    private static readonly HashSet<string> ExpressionStages = new(StringComparer.Ordinal)
    {
        "$project", "$addFields", "$set", "$group", "$replaceRoot", "$replaceWith", "$bucket", "$bucketAuto", "$redact", "$sortByCount",
    };

    private static readonly HashSet<string> WrapperKeys = new(StringComparer.Ordinal)
    {
        "$oid", "$date", "$numberInt", "$numberLong", "$numberDouble", "$numberDecimal", "$uuid", "$binary", "$regularExpression",
        "$timestamp", "$minKey", "$maxKey", "$symbol", "$code", "$dbPointer", "$undefined",
    };

    private readonly Dictionary<string, BindableParameter> _parameters;
    private readonly List<DiagnosticInfo> _diagnostics;
    private readonly Func<int, int, LocationInfo?> _locate;
    private readonly string _label;
    private readonly string _queryId;
    private readonly string _methodName;

    public BsonEmitter(Dictionary<string, BindableParameter> parameters, List<DiagnosticInfo> diagnostics, Func<int, int, LocationInfo?> locate,
        string label, string queryId, string methodName)
    {
        _parameters = parameters;
        _diagnostics = diagnostics;
        _locate = locate;
        _label = label;
        _queryId = queryId;
        _methodName = methodName;
    }

    public bool HasErrors { get; private set; }

    private enum Mode
    {
        Query,
        Expression,
        Literal,
        Integer,
        Forbidden,
    }

    private readonly struct Context(Mode mode, string? where = null, string? meaning = null)
    {
        public Mode Mode { get; } = mode;

        public string? Where { get; } = where;

        public string? Meaning { get; } = meaning;
    }

    /// <summary>Parses <paramref name="json"/>; reports VQ2005/VQ2006 and returns null when it is not valid.</summary>
    public JsonNode? Parse(string json)
    {
        var (node, error) = JsonParser.Parse(json);
        if (error is not null)
        {
            var location = _locate(error.Position, error.Length);
            _diagnostics.Add(error.InKeyPosition
                ? DiagnosticInfo.Create(Descriptors.PlaceholderAsKey, location, _label, _queryId, error.Message)
                : DiagnosticInfo.Create(Descriptors.MalformedJson, location, _label, _queryId, error.Position.ToString(CultureInfo.InvariantCulture), error.Message));
            HasErrors = true;
        }

        return node;
    }

    /// <summary>A filter document: query-language context.</summary>
    public string? Filter(JsonNode node)
    {
        if (node is not JsonObject)
        {
            return Invalid(node, $"{_label} must be a JSON object");
        }

        return Emit(node, new Context(Mode.Query), null);
    }

    /// <summary>A constant document (sort, projection): placeholders are rejected.</summary>
    public string? Constant(JsonNode node, string what)
    {
        if (node is not JsonObject)
        {
            return Invalid(node, $"{_label} must be a JSON object");
        }

        return Emit(node, new Context(Mode.Forbidden, what, "part of the query shape, which must be constant"), null);
    }

    /// <summary>An update document: update operators only (no replacement documents).</summary>
    public string? Update(JsonNode node)
    {
        if (node is not JsonObject update || update.Properties.Count == 0)
        {
            return Invalid(node, $"{_label} must be a non-empty JSON object of update operators such as {{ \"$set\": {{ ... }} }}");
        }

        foreach (var property in update.Properties)
        {
            if (!property.Name.StartsWith("$", StringComparison.Ordinal))
            {
                return Invalid(node, $"{_label} key '{property.Name}' is not an update operator; replacement documents are not supported, use $set");
            }
        }

        return EmitObject(update, key => key switch
        {
            "$rename" => new Context(Mode.Forbidden, "$rename", "a field name"),
            "$pull" => new Context(Mode.Query),
            _ => new Context(Mode.Literal),
        });
    }

    /// <summary>A pipeline: one C# expression per stage.</summary>
    public List<string>? Pipeline(JsonNode node, bool allowWriteStages)
    {
        if (node is not JsonArray array)
        {
            Invalid(node, $"{_label} must be a JSON array of stage documents");
            return null;
        }

        var stages = new List<string>();
        for (var i = 0; i < array.Items.Count; i++)
        {
            var stage = Stage(array.Items[i], i, allowWriteStages);
            if (stage is null)
            {
                return null;
            }

            stages.Add(stage);
        }

        return HasErrors ? null : stages;
    }

    private string? Stage(JsonNode node, int index, bool allowWriteStages)
    {
        if (node is not JsonObject stage || stage.Properties.Count != 1 || !stage.Properties[0].Name.StartsWith("$", StringComparison.Ordinal))
        {
            return Invalid(node, $"stage {index} must be a document with exactly one $-prefixed stage name");
        }

        var name = stage.Properties[0].Name;
        if (!allowWriteStages && name is "$out" or "$merge")
        {
            return Invalid(node, $"write stage {name} is not supported in a read contract");
        }

        if (name == "$facet")
        {
            if (stage.Properties[0].Value is not JsonObject facets)
            {
                return Invalid(node, "$facet must be a document of sub-pipelines");
            }

            var entries = new List<string>();
            foreach (var facet in facets.Properties)
            {
                var sub = Pipeline(facet.Value, allowWriteStages: false);
                if (sub is null)
                {
                    return null;
                }

                entries.Add("{ " + Literal(facet.Name) + ", " + Array(sub) + " }");
            }

            return Document([$"{{ \"$facet\", {Document(entries)} }}"]);
        }

        var context = name switch
        {
            "$match" => new Context(Mode.Query),
            "$limit" or "$skip" or "$sample" => new Context(Mode.Integer),
            _ when ExpressionStages.Contains(name) => new Context(Mode.Expression),
            _ => new Context(Mode.Forbidden, $"stage {name}", "a field name, collection name or stage option"),
        };

        var value = Emit(stage.Properties[0].Value, context, name);
        return value is null ? null : Document([$"{{ {Literal(name)}, {value} }}"]);
    }

    private string? Emit(JsonNode node, Context context, string? parentKey)
    {
        switch (node)
        {
            case JsonPlaceholder placeholder:
                return Placeholder(placeholder, context, parentKey);
            case JsonObject obj:
                if (obj.Properties.Count > 0 && WrapperKeys.Contains(obj.Properties[0].Name))
                {
                    return Wrapper(obj);
                }

                return EmitObject(obj, key => Transition(context, key));
            case JsonArray array:
                var items = new List<string>(array.Items.Count);
                foreach (var item in array.Items)
                {
                    var emitted = Emit(item, context, parentKey);
                    if (emitted is null)
                    {
                        return null;
                    }

                    items.Add(emitted);
                }

                return Array(items);
            case JsonString text:
                return $"new {Bson}BsonString({Literal(text.Value)})";
            case JsonNumber number:
                return Number(number);
            case JsonLiteral literal:
                return literal.Value switch
                {
                    true => Bson + "BsonBoolean.True",
                    false => Bson + "BsonBoolean.False",
                    null => Bson + "BsonNull.Value",
                };
            default:
                return null;
        }
    }

    private string? EmitObject(JsonObject obj, Func<string, Context> childContext)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<string>(obj.Properties.Count);
        foreach (var property in obj.Properties)
        {
            if (!seen.Add(property.Name))
            {
                Report(Descriptors.DuplicateKey, property.NameStart, property.NameEnd - property.NameStart, _label, _queryId, property.Name);
                return null;
            }

            var value = Emit(property.Value, childContext(property.Name), property.Name);
            if (value is null)
            {
                return null;
            }

            entries.Add($"{{ {Literal(property.Name)}, {value} }}");
        }

        return Document(entries);
    }

    private static Context Transition(Context context, string key)
    {
        switch (context.Mode)
        {
            case Mode.Forbidden:
            case Mode.Literal:
            case Mode.Integer:
                return context;
            case Mode.Query:
                return key switch
                {
                    "$where" => new Context(Mode.Forbidden, "$where", "JavaScript code"),
                    "$function" or "$accumulator" => new Context(Mode.Forbidden, key, "JavaScript code"),
                    "$jsonSchema" => new Context(Mode.Forbidden, "$jsonSchema", "part of a schema definition"),
                    "$expr" => new Context(Mode.Expression),
                    _ => context,
                };
            default:
                return key switch
                {
                    "$literal" => new Context(Mode.Literal),
                    "$function" or "$accumulator" => new Context(Mode.Forbidden, key, "JavaScript code"),
                    "$getField" or "$setField" or "$unsetField" => new Context(Mode.Forbidden, key, "a field name"),
                    _ => context,
                };
        }
    }

    private string? Placeholder(JsonPlaceholder placeholder, Context context, string? parentKey)
    {
        _parameters.TryGetValue(placeholder.Name, out var parameter);
        if (parameter is not null)
        {
            parameter.Used = true;
        }

        if (context.Mode == Mode.Forbidden)
        {
            Report(Descriptors.PlaceholderNotAllowed, placeholder.Start, placeholder.End - placeholder.Start,
                _label, _queryId, placeholder.Name, context.Where!, context.Meaning!);
            return null;
        }

        if (parameter is null)
        {
            Report(Descriptors.UnknownPlaceholder, placeholder.Start, placeholder.End - placeholder.Start, _label, _queryId, placeholder.Name, _methodName);
            return null;
        }

        var shape = parameter.Shape;
        if (shape is null)
        {
            ReportParameter(parameter, placeholder.Name, parameter.Reason!);
            return null;
        }

        if (context.Mode == Mode.Integer && !(shape is { Kind: ShapeKind.Scalar, Scalar: "Int32" or "Int64", EnumType: null, Nullable: false }))
        {
            ReportParameter(parameter, placeholder.Name, "this stage requires a literal integer, so the parameter must be a non-nullable int or long");
            return null;
        }

        var value = Bind(shape, "args." + parameter.Field, parameter.Symbol.Name);
        return context.Mode == Mode.Expression && parentKey != "$literal" ? $"{Values}.Literal({value})" : value;
    }

    /// <summary>The C# expression converting a bound argument to a BSON value.</summary>
    private string Bind(ValueShape shape, string access, string name)
    {
        if (shape.Kind == ShapeKind.List)
        {
            var element = shape.Element!;
            return $"{Values}.FromList<{element.DeclaredType}>({access}, static v => {Bind(element, "v", name + "[]")})";
        }

        if (shape.EnumType is not null)
        {
            access = $"({shape.EnumUnderlying}{(shape.Nullable ? "?" : string.Empty)}){access}";
        }

        return shape.Scalar == "DateTime"
            ? $"{Values}.From({access}, {Literal(_queryId)}, {Literal(name)})"
            : $"{Values}.From({access})";
    }

    private string? Number(JsonNumber number)
    {
        var lexeme = number.Lexeme;
        if (number.IsInteger)
        {
            if (int.TryParse(lexeme, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var int32))
            {
                return $"new {Bson}BsonInt32({int32.ToString(CultureInfo.InvariantCulture)})";
            }

            if (long.TryParse(lexeme, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var int64))
            {
                return $"new {Bson}BsonInt64({Int64Literal(int64)})";
            }

            return InvalidExtended(number, $"integer {lexeme} is outside the Int64 range; use {{ \"$numberDecimal\": \"{lexeme}\" }}");
        }

        if (!double.TryParse(lexeme, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || double.IsInfinity(value))
        {
            return InvalidExtended(number, $"number {lexeme} is outside the range of a double");
        }

        return $"new {Bson}BsonDouble({DoubleLiteral(value)})";
    }

    private string? Wrapper(JsonObject obj)
    {
        var key = obj.Properties[0].Name;
        if (obj.Properties.Count != 1)
        {
            return InvalidExtended(obj, $"Extended JSON {key} must be the only key of its object");
        }

        var value = obj.Properties[0].Value;
        if (ContainsPlaceholder(value))
        {
            return InvalidExtended(obj, $"placeholders cannot appear inside Extended JSON {key}; declare the parameter with the CLR type instead (ObjectId, DateTime, long, decimal, Guid, …)");
        }

        switch (key)
        {
            case "$oid":
                return value is JsonString { Value: { Length: 24 } oid } && oid.All(IsHex)
                    ? $"new {Bson}BsonObjectId(new {Bson}ObjectId({Literal(oid.ToLowerInvariant())}))"
                    : InvalidExtended(obj, "$oid must be a string of 24 hexadecimal digits");
            case "$numberInt":
                return value is JsonString numberInt && int.TryParse(numberInt.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var int32)
                    ? $"new {Bson}BsonInt32({int32.ToString(CultureInfo.InvariantCulture)})"
                    : InvalidExtended(obj, "$numberInt must be a string holding a 32-bit integer");
            case "$numberLong":
                return value is JsonString numberLong && long.TryParse(numberLong.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var int64)
                    ? $"new {Bson}BsonInt64({Int64Literal(int64)})"
                    : InvalidExtended(obj, "$numberLong must be a string holding a 64-bit integer");
            case "$numberDouble":
                if (value is JsonString numberDouble)
                {
                    switch (numberDouble.Value)
                    {
                        case "Infinity": return $"new {Bson}BsonDouble(double.PositiveInfinity)";
                        case "-Infinity": return $"new {Bson}BsonDouble(double.NegativeInfinity)";
                        case "NaN": return $"new {Bson}BsonDouble(double.NaN)";
                    }

                    if (double.TryParse(numberDouble.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsInfinity(d))
                    {
                        return $"new {Bson}BsonDouble({DoubleLiteral(d)})";
                    }
                }

                return InvalidExtended(obj, "$numberDouble must be a string holding a double, Infinity, -Infinity or NaN");
            case "$numberDecimal":
                return value is JsonString numberDecimal && IsDecimal128Text(numberDecimal.Value)
                    ? $"new {Bson}BsonDecimal128({Bson}Decimal128.Parse({Literal(numberDecimal.Value)}))"
                    : InvalidExtended(obj, "$numberDecimal must be a string holding a decimal number");
            case "$date":
                return Date(obj, value);
            case "$uuid":
                return value is JsonString uuid && Guid.TryParseExact(uuid.Value, "D", out var guid)
                    ? $"new {Bson}BsonBinaryData(new global::System.Guid({Literal(guid.ToString("D"))}), {Bson}GuidRepresentation.Standard)"
                    : InvalidExtended(obj, "$uuid must be a string in the 8-4-4-4-12 hexadecimal format");
            case "$binary":
                return Binary(obj, value);
            case "$regularExpression":
                if (value is JsonObject regex && regex.Properties.Count == 2
                    && regex.Properties.FirstOrDefault(p => p.Name == "pattern")?.Value is JsonString pattern
                    && regex.Properties.FirstOrDefault(p => p.Name == "options")?.Value is JsonString options
                    && options.Value.All(c => "ilmsux".IndexOf(c) >= 0))
                {
                    return $"new {Bson}BsonRegularExpression({Literal(pattern.Value)}, {Literal(options.Value)})";
                }

                return InvalidExtended(obj, "$regularExpression must be { \"pattern\": string, \"options\": string of i, l, m, s, u, x }");
            case "$timestamp":
                if (value is JsonObject ts && ts.Properties.Count == 2
                    && ts.Properties.FirstOrDefault(p => p.Name == "t")?.Value is JsonNumber { IsInteger: true } t
                    && ts.Properties.FirstOrDefault(p => p.Name == "i")?.Value is JsonNumber { IsInteger: true } i
                    && uint.TryParse(t.Lexeme, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                    && uint.TryParse(i.Lexeme, NumberStyles.None, CultureInfo.InvariantCulture, out var increment))
                {
                    return $"new {Bson}BsonTimestamp(unchecked((int){seconds.ToString(CultureInfo.InvariantCulture)}u), unchecked((int){increment.ToString(CultureInfo.InvariantCulture)}u))";
                }

                return InvalidExtended(obj, "$timestamp must be { \"t\": unsigned integer, \"i\": unsigned integer }");
            case "$minKey":
                return value is JsonNumber { Lexeme: "1" } ? Bson + "BsonMinKey.Value" : InvalidExtended(obj, "$minKey must be 1");
            case "$maxKey":
                return value is JsonNumber { Lexeme: "1" } ? Bson + "BsonMaxKey.Value" : InvalidExtended(obj, "$maxKey must be 1");
            default:
                return InvalidExtended(obj, $"Extended JSON type {key} is deprecated or executable and is not supported");
        }
    }

    private string? Date(JsonObject obj, JsonNode value)
    {
        if (value is JsonObject { Properties.Count: 1 } canonical && canonical.Properties[0].Name == "$numberLong"
            && canonical.Properties[0].Value is JsonString ms
            && long.TryParse(ms.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var canonicalMs))
        {
            return $"new {Bson}BsonDateTime({Int64Literal(canonicalMs)})";
        }

        if (value is JsonString iso)
        {
            var text = iso.Value;
            var hasZone = text.EndsWith("Z", StringComparison.Ordinal)
                || (text.Length > 6 && text[text.Length - 3] == ':' && text[text.Length - 6] is '+' or '-');
            if (hasZone && text.IndexOf('T') > 0
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
            {
                var ticks = instant.UtcTicks - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
                if (ticks % TimeSpan.TicksPerMillisecond != 0)
                {
                    return InvalidExtended(obj, "$date has sub-millisecond precision, which BSON dates cannot store");
                }

                return $"new {Bson}BsonDateTime({Int64Literal(ticks / TimeSpan.TicksPerMillisecond)})";
            }
        }

        return InvalidExtended(obj, "$date must be an ISO-8601 string with 'T' and a 'Z' or ±hh:mm offset, or { \"$numberLong\": \"<milliseconds>\" }");
    }

    private string? Binary(JsonObject obj, JsonNode value)
    {
        if (value is JsonObject binary && binary.Properties.Count == 2
            && binary.Properties.FirstOrDefault(p => p.Name == "base64")?.Value is JsonString base64
            && binary.Properties.FirstOrDefault(p => p.Name == "subType")?.Value is JsonString subType
            && subType.Value.Length is 1 or 2
            && byte.TryParse(subType.Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var kind))
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64.Value);
            }
            catch (FormatException)
            {
                return InvalidExtended(obj, "$binary.base64 is not valid base64");
            }

            if (kind is 3 or 4 && bytes.Length != 16)
            {
                return InvalidExtended(obj, "UUID binary subtypes must hold exactly 16 bytes");
            }

            var hex = string.Join(", ", bytes.Select(b => "0x" + b.ToString("x2", CultureInfo.InvariantCulture)));
            return $"new {Bson}BsonBinaryData(new byte[] {{ {hex} }}, ({Bson}BsonBinarySubType)0x{kind.ToString("x2", CultureInfo.InvariantCulture)})";
        }

        return InvalidExtended(obj, "$binary must be { \"base64\": string, \"subType\": one or two hexadecimal digits }");
    }

    private static bool ContainsPlaceholder(JsonNode node) => node switch
    {
        JsonPlaceholder => true,
        JsonObject obj => obj.Properties.Any(p => ContainsPlaceholder(p.Value)),
        JsonArray array => array.Items.Any(ContainsPlaceholder),
        _ => false,
    };

    private static bool IsDecimal128Text(string text)
    {
        if (text is "Infinity" or "-Infinity" or "NaN")
        {
            return true;
        }

        var (node, error) = JsonParser.Parse(text);
        return error is null && node is JsonNumber;
    }

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static string Document(List<string> entries)
        => entries.Count == 0 ? $"new {Bson}BsonDocument()" : $"new {Bson}BsonDocument {{ {string.Join(", ", entries)} }}";

    private static string Array(List<string> items)
        => items.Count == 0 ? $"new {Bson}BsonArray()" : $"new {Bson}BsonArray {{ {string.Join(", ", items)} }}";

    private static string Literal(string value) => MongoTypeFacts.Literal(value);

    private static string Int64Literal(long value)
        => value == long.MinValue ? "long.MinValue" : value.ToString(CultureInfo.InvariantCulture) + "L";

    private static string DoubleLiteral(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (text.IndexOfAny(['.', 'E', 'e']) < 0)
        {
            text += ".0";
        }

        return text + "d";
    }

    private string? Invalid(JsonNode node, string message)
    {
        _diagnostics.Add(DiagnosticInfo.Create(Descriptors.InvalidContract, _locate(node.Start, node.End - node.Start), _queryId, message));
        HasErrors = true;
        return null;
    }

    private string? InvalidExtended(JsonNode node, string message)
    {
        Report(Descriptors.InvalidExtendedJson, node.Start, node.End - node.Start, _label, _queryId, message);
        return null;
    }

    private void ReportParameter(BindableParameter parameter, string placeholder, string reason)
    {
        HasErrors = true;
        if (parameter.Reported)
        {
            return;
        }

        parameter.Reported = true;
        _diagnostics.Add(DiagnosticInfo.Create(Descriptors.UnbindableParameter, LocationInfo.From(parameter.Symbol.Locations.FirstOrDefault()),
            parameter.Symbol.Name, MongoTypeFacts.Display(parameter.Symbol.Type), placeholder, reason));
    }

    private void Report(DiagnosticDescriptor descriptor, int position, int length, params string[] arguments)
    {
        _diagnostics.Add(DiagnosticInfo.Create(descriptor, _locate(position, length), arguments));
        HasErrors = true;
    }
}
