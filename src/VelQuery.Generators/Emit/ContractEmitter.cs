using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using VelQuery.Generators.Model;

namespace VelQuery.Generators.Emit;

/// <summary>Emits the implementation of one query contract: args struct, binder, descriptor and the method body.</summary>
internal static class ContractEmitter
{
    public const string GeneratedCodeAttribute = "[global::System.CodeDom.Compiler.GeneratedCode(\"VelQuery.Generators\", \"" + VelQuerySqlGenerator.Version + "\")]";

    private const string Nested = "__VelQuery";

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
        => model.Bindings.Count == 0 ? "global::VelQuery.NoArgs" : Nested + ".Args";

    private static void EmitMethod(CodeWriter writer, ContractModel model)
    {
        var method = model.Method;
        var parameters = string.Join(", ", method.Parameters.Select(p => p.Type + " " + p.Name));
        writer.Open($"{method.Accessibility} {(method.IsStatic ? "static " : string.Empty)}partial {method.ReturnType} {method.Name}({parameters})");
        writer.Line($"global::System.ArgumentNullException.ThrowIfNull({method.SessionParameter});");
        var args = model.Bindings.Count == 0
            ? "default(global::VelQuery.NoArgs)"
            : $"new {Nested}.Args({string.Join(", ", model.Bindings.Select(b => b.ParameterName))})";
        var token = method.CancellationTokenParameter ?? "default";
        var call = model.Operation switch
        {
            QueryOperation.Query => "QueryAsync",
            QueryOperation.Stream => "StreamAsync",
            QueryOperation.First => "QueryFirstAsync",
            QueryOperation.FirstOrDefault => "QueryFirstOrDefaultAsync",
            QueryOperation.Single => "QuerySingleAsync",
            QueryOperation.SingleOrDefault => "QuerySingleOrDefaultAsync",
            QueryOperation.Scalar => "ExecuteScalarAsync",
            _ => "ExecuteAsync",
        };
        writer.Line($"return {method.SessionParameter}.{call}({Nested}.Descriptor, {args}, {token});");
        writer.Close();
    }

    private static void EmitNested(CodeWriter writer, ContractModel model)
    {
        writer.Line($"/// <summary>Compiled descriptor and binder for query <c>{Escape(model.QueryId)}</c>.</summary>");
        writer.Line(GeneratedCodeAttribute);
        writer.Open($"private static class {Nested}");

        for (var i = 0; i < model.Bindings.Count; i++)
        {
            writer.Line($"private static readonly global::VelQuery.Parameters.ParameterSpec Spec{i} = {Spec(model.Bindings[i])};");
        }

        if (model.Bindings.Count > 0)
        {
            writer.Line();
        }

        var argsType = ArgsType(model).Replace(Nested + ".", string.Empty);
        var descriptorType = model.Operation == QueryOperation.Execute
            ? $"global::VelQuery.QueryDescriptor<{argsType}>"
            : $"global::VelQuery.QueryDescriptor<{argsType}, {model.RowType}>";
        writer.Line($"internal static readonly {descriptorType} Descriptor = new {descriptorType}(");
        writer.Indent();
        writer.Line(Literal(model.QueryId) + ",");
        writer.Line(Literal(model.Sql) + ",");
        if (model.Operation != QueryOperation.Execute)
        {
            writer.Line(model.RowFactory + ",");
        }

        writer.Line((model.Bindings.Count == 0 ? "null" : "Bind") + ",");
        writer.Line("global::System.Data.CommandType." + model.CommandType + ",");
        writer.Line(model.TimeoutSeconds > 0
            ? $"global::System.TimeSpan.FromSeconds({model.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)}));"
            : "null);");
        writer.Outdent();

        if (model.Bindings.Count > 0)
        {
            writer.Line();
            writer.Open("private static void Bind(global::System.Data.Common.DbCommand command, global::VelQuery.Providers.IVelQueryProvider provider, Args args)");
            for (var i = 0; i < model.Bindings.Count; i++)
            {
                writer.Line($"global::VelQuery.Parameters.ParameterWriter.Add(command, provider, in Spec{i}, {Value(model.Bindings[i])});");
            }

            writer.Close();
            writer.Line();
            writer.Open("internal readonly struct Args");
            foreach (var binding in model.Bindings)
            {
                writer.Line($"public readonly {binding.Type} {binding.FieldName};");
            }

            writer.Line();
            writer.Open($"public Args({string.Join(", ", model.Bindings.Select(b => b.Type + " " + b.FieldName))})");
            foreach (var binding in model.Bindings)
            {
                writer.Line($"this.{binding.FieldName} = {binding.FieldName};");
            }

            writer.Close();
            writer.Close();
        }

        writer.Close();
    }

    private static string Spec(BindingModel binding)
    {
        var text = "new global::VelQuery.Parameters.ParameterSpec(" + Literal(binding.SqlName);
        if (binding.DbType is not null)
        {
            text += ", DbType: global::System.Data.DbType." + binding.DbType;
        }

        if (binding.Size is { } size)
        {
            text += ", Size: " + size.ToString(CultureInfo.InvariantCulture);
        }

        if (binding.Precision is { } precision)
        {
            text += ", Precision: " + precision.ToString(CultureInfo.InvariantCulture);
        }

        if (binding.Scale is { } scale)
        {
            text += ", Scale: " + scale.ToString(CultureInfo.InvariantCulture);
        }

        return text + ")";
    }

    private static string Value(BindingModel binding)
    {
        var field = "args." + binding.FieldName;
        if (binding.EnumUnderlyingType is null)
        {
            return field;
        }

        // Enums bind as their integral value: drivers do not accept arbitrary enum types.
        return binding.IsNullableValueType
            ? $"{field}.HasValue ? ({binding.EnumUnderlyingType}){field}.GetValueOrDefault() : null"
            : $"({binding.EnumUnderlyingType}){field}";
    }

    public static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    private static string Escape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r", " ").Replace("\n", " ");
}
