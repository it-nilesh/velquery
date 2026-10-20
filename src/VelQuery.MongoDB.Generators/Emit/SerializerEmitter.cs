using System.Globalization;
using System.Linq;
using VelQuery.MongoDB.Generators.Analysis;
using VelQuery.MongoDB.Generators.Model;

namespace VelQuery.MongoDB.Generators.Emit;

/// <summary>
/// Emits a reflection-free <c>VelQueryBsonSerializer&lt;T&gt;</c> for one document type: fields are matched by name
/// with a shared allocation-free name decoder, read with typed strict helpers, unknown fields skipped, duplicates and
/// missing required fields rejected with the field path.
/// </summary>
internal static class SerializerEmitter
{
    private const string Reading = "global::VelQuery.MongoDB.Serialization.MongoBsonReading";
    private const string Writing = "global::VelQuery.MongoDB.Serialization.MongoBsonWriting";
    private const string Generated = "global::VelQuery.Generated.Mongo.";

    public static string Emit(DocumentPlan plan)
    {
        var writer = CodeWriter.WithHeader();
        writer.Open("namespace VelQuery.Generated.Mongo");
        writer.Line($"/// <summary>Reflection-free BSON serializer for <c>{plan.Type.Replace("<", "&lt;").Replace(">", "&gt;")}</c>.</summary>");
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Open($"internal sealed class {plan.SerializerName} : global::VelQuery.MongoDB.Serialization.VelQueryBsonSerializer<{plan.Type}>");
        writer.Line($"public static readonly {plan.SerializerName} Instance = new();");
        writer.Line();
        writer.Line($"private static readonly global::VelQuery.MongoDB.Serialization.MongoFieldNames Fields = new({string.Join(", ", plan.Fields.Select(f => MongoTypeFacts.Literal(f.Field)))});");
        writer.Line();
        EmitRead(writer, plan);
        writer.Line();
        EmitWrite(writer, plan);
        writer.Close();
        writer.Close();
        return writer.ToString();
    }

    private static void EmitRead(CodeWriter writer, DocumentPlan plan)
    {
        writer.Open($"public override {plan.Type} Read(global::MongoDB.Bson.IO.IBsonReader reader, string? queryId, string? path)");
        writer.Line("global::System.ArgumentNullException.ThrowIfNull(reader);");
        writer.Line("reader.ReadStartDocument();");
        for (var i = 0; i < plan.Fields.Count; i++)
        {
            writer.Line($"{plan.Fields[i].Shape.DeclaredType} m{N(i)} = default!;");
            writer.Line($"var h{N(i)} = false;");
        }

        writer.Open("while (reader.ReadBsonType() != global::MongoDB.Bson.BsonType.EndOfDocument)");
        writer.Open("switch (reader.ReadName(Fields))");
        for (var i = 0; i < plan.Fields.Count; i++)
        {
            var field = plan.Fields[i];
            var name = MongoTypeFacts.Literal(field.Field);
            writer.Line($"case {name}:");
            writer.Indent();
            writer.Open($"if (h{N(i)})");
            writer.Line($"throw {Reading}.Duplicate(queryId, path, {name}, typeof({field.Shape.BareType}));");
            writer.Close();
            writer.Line();
            writer.Line($"m{N(i)} = {Read(field.Shape, "reader", "queryId", "path", name, MongoTypeFacts.Literal(field.Field + "[]"), 0)};");
            writer.Line($"h{N(i)} = true;");
            writer.Line("break;");
            writer.Outdent();
        }

        writer.Line("default:");
        writer.Indent();
        writer.Line("reader.SkipValue();");
        writer.Line("break;");
        writer.Outdent();
        writer.Close();
        writer.Close();
        writer.Line();
        writer.Line("reader.ReadEndDocument();");
        for (var i = 0; i < plan.Fields.Count; i++)
        {
            var field = plan.Fields[i];
            if (field.Role != FieldRole.Optional && !field.Shape.Nullable)
            {
                writer.Open($"if (!h{N(i)})");
                writer.Line($"throw {Reading}.Missing(queryId, path, {MongoTypeFacts.Literal(field.Field)}, typeof({field.Shape.BareType}));");
                writer.Close();
                writer.Line();
            }
        }

        var indexed = plan.Fields.Select((f, i) => (Field: f, Index: i)).ToList();
        var arguments = string.Join(", ", indexed.Where(x => x.Field.Role == FieldRole.Constructor).OrderBy(x => x.Field.ConstructorIndex).Select(x => "m" + N(x.Index)));
        var initializers = indexed.Where(x => x.Field.Role == FieldRole.Initializer).Select(x => $"{x.Field.Property} = m{N(x.Index)}").ToList();
        var construct = $"new {plan.Type}({arguments})" + (initializers.Count == 0 ? string.Empty : " { " + string.Join(", ", initializers) + " }");
        var optional = indexed.Where(x => x.Field.Role == FieldRole.Optional).ToList();
        if (optional.Count == 0)
        {
            writer.Line($"return {construct};");
        }
        else
        {
            writer.Line($"var result = {construct};");
            foreach (var (field, index) in optional)
            {
                writer.Open($"if (h{N(index)})");
                writer.Line($"result.{field.Property} = m{N(index)};");
                writer.Close();
            }

            writer.Line();
            writer.Line("return result;");
        }

        writer.Close();
    }

    private static void EmitWrite(CodeWriter writer, DocumentPlan plan)
    {
        writer.Open($"public override void Write(global::MongoDB.Bson.IO.IBsonWriter writer, {plan.Type} value, string? queryId, string? path)");
        writer.Line("global::System.ArgumentNullException.ThrowIfNull(writer);");
        writer.Line("writer.WriteStartDocument();");
        foreach (var field in plan.Fields)
        {
            var access = "value." + field.Property;
            var name = MongoTypeFacts.Literal(field.Field);
            var condition = field.OmitEmptyObjectId
                ? $"{access} != global::MongoDB.Bson.ObjectId.Empty"
                : field.IgnoreIfNull ? $"{access} is not null" : null;
            if (condition is not null)
            {
                // An empty ObjectId _id is omitted so the server assigns one (VelQuery never mutates the document).
                writer.Open($"if ({condition})");
            }

            writer.Line($"writer.WriteName({name});");
            writer.Line(Write(field.Shape, "writer", access, "queryId", "path", name, MongoTypeFacts.Literal(field.Field + "[]"), 0) + ";");
            if (condition is not null)
            {
                writer.Close();
            }
        }

        writer.Line("writer.WriteEndDocument();");
        writer.Close();
    }

    private static string Read(ValueShape shape, string r, string q, string p, string f, string elementField, int depth)
    {
        switch (shape.Kind)
        {
            case ShapeKind.Scalar:
                var call = $"{Reading}.{(shape.Nullable ? "ReadNullable" : "Read")}{shape.Scalar}({r}, {q}, {p}, {f})";
                return shape.EnumType is null ? call : $"({shape.DeclaredType}){call}";
            case ShapeKind.Document:
                return $"{Reading}.{(shape.Nullable ? "ReadNullableDocument" : "ReadDocument")}({r}, {Generated}{shape.Serializer}.Instance, {q}, {p}, {f})";
            default:
                var element = shape.Element!;
                var d = (depth + 1).ToString(CultureInfo.InvariantCulture);
                var elementRead = Read(element, "r" + d, "q" + d, "p" + d, "f" + d, $"f{d} + \"[]\"", depth + 1);
                var list = $"{Reading}.{(shape.Nullable ? "ReadNullableList" : "ReadList")}<{element.DeclaredType}>({r}, {q}, {p}, {f}, {elementField}, static (r{d}, q{d}, p{d}, f{d}) => {elementRead})";
                return shape.IsArray ? list + (shape.Nullable ? "?.ToArray()" : ".ToArray()") : list;
        }
    }

    private static string Write(ValueShape shape, string w, string v, string q, string p, string f, string elementField, int depth)
    {
        switch (shape.Kind)
        {
            case ShapeKind.Scalar:
                var value = shape.EnumType is null ? v : $"({shape.EnumUnderlying}{(shape.Nullable ? "?" : string.Empty)}){v}";
                return $"{Writing}.{(shape.Nullable ? "WriteNullable" : "Write")}{shape.Scalar}({w}, {value}, {q}, {p}, {f})";
            case ShapeKind.Document:
                return $"{Writing}.{(shape.Nullable ? "WriteNullableDocument" : "WriteDocument")}({w}, {Generated}{shape.Serializer}.Instance, {v}, {q}, {p}, {f})";
            default:
                var element = shape.Element!;
                var d = (depth + 1).ToString(CultureInfo.InvariantCulture);
                var elementWrite = Write(element, "w" + d, "v" + d, "q" + d, "p" + d, "f" + d, $"f{d} + \"[]\"", depth + 1);
                return $"{Writing}.{(shape.Nullable ? "WriteNullableList" : "WriteList")}<{element.DeclaredType}>({w}, {v}, {q}, {p}, {f}, {elementField}, static (w{d}, v{d}, q{d}, p{d}, f{d}) => {elementWrite})";
        }
    }

    private static string N(int index) => index.ToString(CultureInfo.InvariantCulture);
}
