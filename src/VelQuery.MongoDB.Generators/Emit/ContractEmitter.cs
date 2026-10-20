using System.Globalization;
using System.Linq;
using VelQuery.MongoDB.Generators.Analysis;
using VelQuery.MongoDB.Generators.Model;

namespace VelQuery.MongoDB.Generators.Emit;

/// <summary>
/// Emits one MongoDB contract: a nested holder with the typed arguments struct, the binders that build BSON directly
/// from those arguments, the statically cached descriptor, and the partial method body.
/// </summary>
internal static class ContractEmitter
{
    private const string Nested = "__VelQuery";
    private const string Mongo = "global::VelQuery.MongoDB.";
    private const string BsonDocument = "global::MongoDB.Bson.BsonDocument";

    public static string Emit(ContractModel model)
    {
        var writer = CodeWriter.WithHeader();
        if (model.Namespace is not null)
        {
            writer.Open("namespace " + model.Namespace);
        }

        foreach (var type in model.ContainingTypes)
        {
            writer.Open(Declaration(type));
        }

        writer.Open(Declaration(model.ContractType));
        EmitMethod(writer, model);
        writer.Line();
        EmitNested(writer, model);
        writer.Close();

        foreach (var _ in model.ContainingTypes)
        {
            writer.Close();
        }

        if (model.Namespace is not null)
        {
            writer.Close();
        }

        return writer.ToString();
    }

    private static string Declaration(TypeDeclarationModel type)
        => (type.IsStatic ? "static " : string.Empty) + "partial " + type.Keyword + " " + type.Name;

    private static string ArgsType(ContractModel model)
        => model.Arguments.Count == 0 ? Mongo + "MongoNoArgs" : Nested + ".Args";

    private static void EmitMethod(CodeWriter writer, ContractModel model)
    {
        var method = model.Method;
        var parameters = string.Join(", ", method.Parameters.Select(p => p.Type + " " + p.Name));
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Open($"{method.Accessibility} {(method.IsStatic ? "static " : string.Empty)}partial {method.ReturnType} {method.Name}({parameters})");
        writer.Line($"global::System.ArgumentNullException.ThrowIfNull({method.SessionName});");
        var token = method.TokenName ?? "default";
        var args = model.Arguments.Count == 0
            ? $"default({Mongo}MongoNoArgs)"
            : $"new {Nested}.Args({string.Join(", ", model.Arguments.Select(a => a.Parameter))})";
        var aggregate = model.Kind == ContractKind.Aggregate;
        var call = model.Operation switch
        {
            Operation.List => aggregate ? "AggregateAsync" : "FindAsync",
            Operation.Stream => aggregate ? "AggregateStreamAsync" : "FindStreamAsync",
            Operation.First => aggregate ? "AggregateFirstAsync" : "FindFirstAsync",
            Operation.FirstOrDefault => aggregate ? "AggregateFirstOrDefaultAsync" : "FindFirstOrDefaultAsync",
            Operation.Single => aggregate ? "AggregateSingleAsync" : "FindSingleAsync",
            Operation.SingleOrDefault => aggregate ? "AggregateSingleOrDefaultAsync" : "FindSingleOrDefaultAsync",
            Operation.Count => "CountAsync",
            Operation.Update => "UpdateAsync",
            Operation.Delete => "DeleteAsync",
            Operation.InsertOne => "InsertOneAsync",
            _ => "InsertManyAsync",
        };

        if (model.Operation is Operation.InsertOne or Operation.InsertMany)
        {
            writer.Line($"return {method.SessionName}.{call}<{model.DocumentType}>({Nested}.Descriptor, {model.DocumentParameter}, {token});");
        }
        else if (model.DocumentType is null)
        {
            writer.Line($"return {method.SessionName}.{call}<{ArgsType(model)}>({Nested}.Descriptor, {args}, {token});");
        }
        else
        {
            writer.Line($"return {method.SessionName}.{call}<{ArgsType(model)}, {model.DocumentType}>({Nested}.Descriptor, {args}, {token});");
        }

        writer.Close();
    }

    private static void EmitNested(CodeWriter writer, ContractModel model)
    {
        writer.Line($"/// <summary>Compiled descriptor and binders for MongoDB query <c>{Escape(model.QueryId)}</c>.</summary>");
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Open($"private static class {Nested}");
        var argsType = ArgsType(model);
        writer.Line($"internal static readonly {DescriptorType(model, argsType)} Descriptor = new(");
        writer.Indent();
        writer.Line(DescriptorArguments(model) + ");");
        writer.Outdent();

        if (model.FilterExpression is not null)
        {
            writer.Line();
            writer.Line($"private static {BsonDocument} Filter(in {argsType} args) => {model.FilterExpression};");
        }

        if (model.UpdateExpression is not null)
        {
            writer.Line();
            writer.Line($"private static {BsonDocument} Update(in {argsType} args) => {model.UpdateExpression};");
        }

        if (model.SortExpression is not null)
        {
            writer.Line();
            writer.Line($"private static {BsonDocument} Sort() => {model.SortExpression};");
        }

        if (model.ProjectionExpression is not null)
        {
            writer.Line();
            writer.Line($"private static {BsonDocument} Projection() => {model.ProjectionExpression};");
        }

        if (model.Kind == ContractKind.Aggregate)
        {
            writer.Line();
            if (model.PipelineStages.Count == 0)
            {
                writer.Line($"private static {BsonDocument}[] Pipeline(in {argsType} args) => new {BsonDocument}[0];");
            }
            else
            {
                writer.Line($"private static {BsonDocument}[] Pipeline(in {argsType} args) => new {BsonDocument}[]");
                writer.Line("{");
                writer.Indent();
                foreach (var stage in model.PipelineStages)
                {
                    writer.Line(stage + ",");
                }

                writer.Outdent();
                writer.Line("};");
            }
        }

        if (model.Arguments.Count > 0)
        {
            writer.Line();
            writer.Open("internal readonly struct Args");
            var parameters = string.Join(", ", model.Arguments.Select(a => a.Type + " " + a.Parameter));
            writer.Open($"public Args({parameters})");
            foreach (var argument in model.Arguments)
            {
                writer.Line($"this.{argument.Field} = {argument.Parameter};");
            }

            writer.Close();
            writer.Line();
            foreach (var argument in model.Arguments)
            {
                writer.Line($"public readonly {argument.Type} {argument.Field};");
            }

            writer.Close();
        }

        writer.Close();
    }

    private static string DescriptorType(ContractModel model, string argsType) => model.Kind switch
    {
        ContractKind.Find => $"{Mongo}MongoFindDescriptor<{argsType}, {model.DocumentType}>",
        ContractKind.Count => $"{Mongo}MongoCountDescriptor<{argsType}>",
        ContractKind.Aggregate => $"{Mongo}MongoAggregateDescriptor<{argsType}, {model.DocumentType}>",
        ContractKind.Update => $"{Mongo}MongoUpdateDescriptor<{argsType}>",
        ContractKind.Delete => $"{Mongo}MongoDeleteDescriptor<{argsType}>",
        _ => $"{Mongo}MongoInsertDescriptor<{model.DocumentType}>",
    };

    private static string DescriptorArguments(ContractModel model)
    {
        var head = MongoTypeFacts.Literal(model.QueryId) + ", " + MongoTypeFacts.Literal(model.Collection);
        var serializer = model.Serializer is null ? null : "global::VelQuery.Generated.Mongo." + model.Serializer + ".Instance";
        var timeout = "timeout: " + (model.TimeoutMilliseconds > 0
            ? "global::System.TimeSpan.FromMilliseconds(" + model.TimeoutMilliseconds.ToString(CultureInfo.InvariantCulture) + ")"
            : "null");
        static string Optional(int value) => value > 0 ? value.ToString(CultureInfo.InvariantCulture) : "null";
        return model.Kind switch
        {
            ContractKind.Find => $"{head}, Filter, {serializer}, sort: {(model.SortExpression is null ? "null" : "Sort")}, projection: {(model.ProjectionExpression is null ? "null" : "Projection")}, limit: {Optional(model.Limit)}, skip: {Optional(model.Skip)}, {timeout}",
            ContractKind.Count => $"{head}, Filter, limit: {Optional(model.Limit)}, skip: {Optional(model.Skip)}, {timeout}",
            ContractKind.Aggregate => $"{head}, Pipeline, {serializer}, allowDiskUse: {Bool(model.AllowDiskUse)}, {timeout}",
            ContractKind.Update => $"{head}, Filter, Update, many: {Bool(model.Many)}, upsert: {Bool(model.Upsert)}, {timeout}",
            ContractKind.Delete => $"{head}, Filter, many: {Bool(model.Many)}, {timeout}",
            _ => $"{head}, {serializer}, {timeout}",
        };
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
