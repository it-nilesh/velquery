using System.Collections.Generic;
using System.Linq;
using VelQuery.Generators.Infrastructure;
using VelQuery.Generators.Model;

namespace VelQuery.Generators.Emit;

/// <summary>
/// Emits a compile-time <c>IRowMapperFactory&lt;T&gt;</c> (PRD §9.3). <c>Create</c> resolves ordinals by name once per
/// reader shape, rejects duplicate matching columns and fails fast on missing required columns. <c>Map</c> uses typed
/// getters when the provider field type matches and lossless <c>ValueConverter</c> conversion otherwise, with strict
/// NULL checks driven by the member's nullability.
/// </summary>
internal static class RowMapperEmitter
{
    private const string Reader = "global::System.Data.Common.DbDataReader";
    private const string MappingException = "global::VelQuery.VelQueryMappingException";

    public static string Emit(RowPlan plan)
    {
        var members = plan.Members;
        var writer = CodeWriter.WithHeader();
        writer.Open("namespace VelQuery.Generated");
        writer.Line($"/// <summary>Generated row mapper factory for <see cref=\"{plan.Type.Replace("global::", string.Empty)}\"/>.</summary>");
        writer.Line(ContractEmitter.GeneratedCodeAttribute);
        writer.Open($"internal sealed class {plan.FactoryName} : global::VelQuery.Mapping.IRowMapperFactory<{plan.Type}>");
        writer.Line($"public static readonly {plan.FactoryName} Instance = new {plan.FactoryName}();");
        writer.Line();
        writer.Open($"private {plan.FactoryName}()");
        writer.Close();
        writer.Line();
        writer.Open($"public global::VelQuery.Mapping.IRowMapper<{plan.Type}> Create({Reader} reader, in global::VelQuery.Mapping.MappingContext context)");
        writer.Line("global::System.ArgumentNullException.ThrowIfNull(reader);");
        writer.Line("return new Mapper(reader, context);");
        writer.Close();
        writer.Line();

        writer.Open($"private sealed class Mapper : global::VelQuery.Mapping.IRowMapper<{plan.Type}>");
        writer.Line("private readonly string? _queryId;");
        writer.Line("private readonly bool _strictNulls;");
        for (var i = 0; i < members.Count; i++)
        {
            writer.Line($"private readonly int _o{i};");
            writer.Line($"private readonly bool _f{i};");
        }

        writer.Line();
        EmitConstructor(writer, members);
        writer.Line();
        EmitMap(writer, plan);
        writer.Line();
        writer.Open("private int Match(int current, int ordinal, string column, global::System.Type type)");
        writer.Open("if (current >= 0)");
        writer.Line($"throw new {MappingException}(_queryId, column, type, \"the result contains more than one column matching this member; alias the columns so each member matches exactly one.\");");
        writer.Close();
        writer.Line();
        writer.Line("return ordinal;");
        writer.Close();
        writer.Line();
        writer.Open("private T OnNull<T>(string column)");
        writer.Open("if (_strictNulls)");
        writer.Line($"throw {MappingException}.NullToNonNullable(_queryId, column, typeof(T));");
        writer.Close();
        writer.Line();
        writer.Line("return default!;");
        writer.Close();
        writer.Line();
        writer.Line($"private T Convert<T>({Reader} reader, int ordinal, string column)");
        writer.Line($"    => global::VelQuery.Mapping.ValueConverter.Convert<T>(reader.GetValue(ordinal), _queryId, column, _strictNulls);");
        writer.Close();
        writer.Close();
        writer.Close();
        return writer.ToString();
    }

    private static void EmitConstructor(CodeWriter writer, EquatableArray<RowMemberModel> members)
    {
        writer.Open($"public Mapper({Reader} reader, in global::VelQuery.Mapping.MappingContext context)");
        writer.Line("_queryId = context.QueryId;");
        writer.Line("_strictNulls = context.StrictNulls;");
        for (var i = 0; i < members.Count; i++)
        {
            writer.Line($"var o{i} = -1;");
        }

        writer.Open("for (var i = 0; i < reader.FieldCount; i++)");
        writer.Line("var name = reader.GetName(i);");
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            writer.Open($"{(i == 0 ? "if" : "else if")} (string.Equals(name, {ContractEmitter.Literal(member.Column)}, global::System.StringComparison.OrdinalIgnoreCase))");
            writer.Line($"o{i} = Match(o{i}, i, {ContractEmitter.Literal(member.Column)}, typeof({member.ValueType}));");
            writer.Close();
        }

        writer.Close();
        writer.Line();
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            if (member.Kind == MemberKind.Optional)
            {
                continue;
            }

            writer.Open($"if (o{i} < 0)");
            writer.Line($"throw {MappingException}.MissingColumn(_queryId, {ContractEmitter.Literal(member.Column)}, typeof({member.ValueType}));");
            writer.Close();
            writer.Line();
        }

        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var fast = string.Join(" || ", member.FastTypes.Select(t => $"t{i} == typeof({t})"));
            writer.Line($"_o{i} = o{i};");
            var fieldType = member.Kind == MemberKind.Optional ? $"o{i} >= 0 ? reader.GetFieldType(o{i}) : null" : $"reader.GetFieldType(o{i})";
            writer.Line($"var t{i} = {fieldType};");
            writer.Line($"_f{i} = {fast};");
        }

        writer.Close();
    }

    private static void EmitMap(CodeWriter writer, RowPlan plan)
    {
        var members = plan.Members;
        writer.Open($"public {plan.Type} Map({Reader} reader)");
        var ctorArgs = new List<string>();
        var initializers = new List<string>();
        for (var i = 0; i < members.Count; i++)
        {
            switch (members[i].Kind)
            {
                case MemberKind.Constructor:
                    ctorArgs.Add(Read(members[i], i));
                    break;
                case MemberKind.Initializer:
                    initializers.Add($"{members[i].Name} = {Read(members[i], i)},");
                    break;
            }
        }

        var terminator = initializers.Count == 0 ? ";" : string.Empty;
        if (ctorArgs.Count == 0)
        {
            writer.Line($"var result = new {plan.Type}(){terminator}");
        }
        else
        {
            writer.Line($"var result = new {plan.Type}(");
            writer.Indent();
            for (var i = 0; i < ctorArgs.Count; i++)
            {
                writer.Line(ctorArgs[i] + (i == ctorArgs.Count - 1 ? ")" + terminator : ","));
            }

            writer.Outdent();
        }

        if (initializers.Count > 0)
        {
            writer.Line("{");
            writer.Indent();
            foreach (var initializer in initializers)
            {
                writer.Line(initializer);
            }

            writer.Outdent();
            writer.Line("};");
        }

        for (var i = 0; i < members.Count; i++)
        {
            if (members[i].Kind != MemberKind.Optional)
            {
                continue;
            }

            writer.Open($"if (_o{i} >= 0)");
            writer.Line($"result.{members[i].Name} = {Read(members[i], i)};");
            writer.Close();
        }

        writer.Line("return result;");
        writer.Close();
    }

    private static string Read(RowMemberModel member, int index)
    {
        var typed = $"reader.{member.Getter}(_o{index})";
        if (member.EnumCast is not null)
        {
            typed = $"({member.EnumCast}){typed}";
        }

        var value = $"(_f{index} ? {typed} : Convert<{member.ValueType}>(reader, _o{index}, {ContractEmitter.Literal(member.Column)}))";
        var whenNull = member.IsNullable
            ? $"default({member.Type})"
            : $"OnNull<{member.ValueType}>({ContractEmitter.Literal(member.Column)})";
        return $"reader.IsDBNull(_o{index}) ? {whenNull} : {value}";
    }
}
