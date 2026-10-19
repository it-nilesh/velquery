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
/// <para>
/// Mappers always read columns in increasing ordinal order, so the factory reports
/// <c>SupportsSequentialAccess</c> and VelQuery can use <c>CommandBehavior.SequentialAccess</c>. When the SELECT order
/// matches the member read order (the usual case) <c>Map</c> reads directly; otherwise it reads into locals in ordinal
/// order and then constructs the row.
/// </para>
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
        writer.Line("public bool SupportsSequentialAccess => true;");
        writer.Line();

        writer.Open($"private sealed class Mapper : global::VelQuery.Mapping.IRowMapper<{plan.Type}>");
        writer.Line("private readonly string? _queryId;");
        writer.Line("private readonly bool _strictNulls;");
        writer.Line("private readonly bool _nativeConversions;");
        writer.Line("private readonly bool _ordered;");
        writer.Line("private readonly int[]? _readOrder;");
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
        EmitMapUnordered(writer, plan);
        writer.Line();
        writer.Open("private static int[] SortByOrdinal(int[] ordinals)");
        writer.Line("var count = 0;");
        writer.Open("foreach (var ordinal in ordinals)");
        writer.Open("if (ordinal >= 0)");
        writer.Line("count++;");
        writer.Close();
        writer.Close();
        writer.Line();
        writer.Line("var members = new int[count];");
        writer.Line("var next = 0;");
        writer.Open("for (var i = 0; i < ordinals.Length; i++)");
        writer.Open("if (ordinals[i] >= 0)");
        writer.Line("members[next++] = i;");
        writer.Close();
        writer.Close();
        writer.Line();
        writer.Line("global::System.Array.Sort(members, (x, y) => ordinals[x].CompareTo(ordinals[y]));");
        writer.Line("return members;");
        writer.Close();
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
        writer.Line("// Providers with native conversions (e.g. SQLite TEXT dates) convert in their typed getter; otherwise only");
        writer.Line("// lossless ValueConverter rules apply.");
        writer.Line($"private T Convert<T>({Reader} reader, int ordinal, string column)");
        writer.Line($"    => _nativeConversions ? reader.GetFieldValue<T>(ordinal) : global::VelQuery.Mapping.ValueConverter.Convert<T>(reader.GetValue(ordinal), _queryId, column, _strictNulls);");
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
        writer.Line("_nativeConversions = context.NativeConversions;");
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

        writer.Line();
        writer.Line("var ordered = true;");
        writer.Line("var last = -1;");
        foreach (var i in ReadSequence(members))
        {
            writer.Open($"if (o{i} >= 0)");
            writer.Line($"ordered &= o{i} > last;");
            writer.Line($"last = o{i};");
            writer.Close();
        }

        writer.Line("_ordered = ordered;");
        var all = string.Join(", ", Enumerable.Range(0, members.Count).Select(i => $"o{i}"));
        writer.Line($"_readOrder = ordered ? null : SortByOrdinal(new int[] {{ {all} }});");
        writer.Close();
    }

    /// <summary>The order in which the direct <c>Map</c> reads members: constructor, initializers, then optional.</summary>
    private static IEnumerable<int> ReadSequence(EquatableArray<RowMemberModel> members)
    {
        foreach (var kind in new[] { MemberKind.Constructor, MemberKind.Initializer, MemberKind.Optional })
        {
            for (var i = 0; i < members.Count; i++)
            {
                if (members[i].Kind == kind)
                {
                    yield return i;
                }
            }
        }
    }

    /// <summary>
    /// Emits the strict fast path. Non-nullable members call the typed getter without a preceding <c>IsDBNull</c> (the
    /// same work as hand-written ADO.NET); if a getter throws, an exception filter decides whether the column was NULL
    /// (via <c>IsDBNull</c> where the driver allows it after a failed read, otherwise from the cast failure) and reports
    /// the strict NULL error. Non-strict sessions use
    /// <c>MapChecked</c>, which tests every column, because a NULL there is data rather than an error.
    /// </summary>
    private static void EmitMap(CodeWriter writer, RowPlan plan)
    {
        var members = plan.Members;
        writer.Open($"public {plan.Type} Map({Reader} reader)");
        writer.Open("if (!_ordered)");
        writer.Line("return MapUnordered(reader);");
        writer.Close();
        writer.Line();
        writer.Open("if (!_strictNulls)");
        writer.Line("return MapChecked(reader);");
        writer.Close();
        writer.Line();
        writer.Line("var member = -1;");
        writer.Open("try");
        foreach (var i in ReadSequence(members))
        {
            var member = members[i];
            var value = member.IsNullable ? Read(member, i) : ReadNonNull(member, i);
            if (member.Kind == MemberKind.Optional)
            {
                writer.Line($"{member.Type} v{i} = default!;");
                writer.Open($"if (_o{i} >= 0)");
                writer.Line($"member = {i};");
                writer.Line($"v{i} = {value};");
                writer.Close();
            }
            else
            {
                writer.Line($"member = {i};");
                writer.Line($"{member.Type} v{i} = {value};");
            }
        }

        writer.Line();
        writer.Line("// Reads are done; failures from here on (e.g. the row type's constructor) are not NULL errors.");
        writer.Line("member = -1;");
        EmitConstruction(writer, plan, i => $"v{i}");
        writer.Close();
        writer.Open("catch (global::System.Exception ex) when (member >= 0 && IsNullFailure(reader, member, ex))");
        writer.Line("throw NullError(member);");
        writer.Close();
        writer.Close();
        writer.Line();

        writer.Open($"private {plan.Type} MapChecked({Reader} reader)");
        EmitConstruction(writer, plan, i => Read(members[i], i));
        writer.Close();
        writer.Line();

        writer.Line("// Under SequentialAccess some drivers (Npgsql) reject IsDBNull on a column whose getter already failed. The fast");
        writer.Line("// path only calls a typed getter when the field type matches exactly, so a cast failure there can only mean NULL.");
        writer.Open($"private bool IsNullFailure({Reader} reader, int member, global::System.Exception exception)");
        writer.Open("try");
        writer.Line("return reader.IsDBNull(OrdinalOf(member));");
        writer.Close();
        writer.Open("catch (global::System.Exception)");
        writer.Line("return exception is global::System.InvalidCastException or global::System.Data.SqlTypes.SqlNullValueException;");
        writer.Close();
        writer.Close();
        writer.Line();

        writer.Open("private int OrdinalOf(int member)");
        writer.Open("switch (member)");
        for (var i = 0; i < members.Count; i++)
        {
            writer.Line($"case {i}: return _o{i};");
        }

        writer.Line("default: return -1;");
        writer.Close();
        writer.Close();
        writer.Line();

        writer.Open($"private {MappingException} NullError(int member)");
        writer.Open("switch (member)");
        for (var i = 0; i < members.Count; i++)
        {
            writer.Line($"case {i}: return {MappingException}.NullToNonNullable(_queryId, {ContractEmitter.Literal(members[i].Column)}, typeof({members[i].ValueType}));");
        }

        writer.Line($"default: throw new global::System.ArgumentOutOfRangeException(nameof(member));");
        writer.Close();
        writer.Close();
    }

    /// <summary>A read that assumes NOT NULL; NULL surfaces as a getter exception handled by the caller's filter.</summary>
    private static string ReadNonNull(RowMemberModel member, int index)
    {
        var typed = $"reader.{member.Getter}(_o{index})";
        if (member.EnumCast is not null)
        {
            typed = $"({member.EnumCast}){typed}";
        }

        return $"(_f{index} ? {typed} : Convert<{member.ValueType}>(reader, _o{index}, {ContractEmitter.Literal(member.Column)}))";
    }

    private static void EmitMapUnordered(CodeWriter writer, RowPlan plan)
    {
        var members = plan.Members;
        writer.Open($"private {plan.Type} MapUnordered({Reader} reader)");
        for (var i = 0; i < members.Count; i++)
        {
            writer.Line($"{members[i].Type} v{i} = default!;");
        }

        writer.Open("foreach (var member in _readOrder!)");
        writer.Open("switch (member)");
        for (var i = 0; i < members.Count; i++)
        {
            writer.Line($"case {i}:");
            writer.Indent();
            writer.Line($"v{i} = {Read(members[i], i)};");
            writer.Line("break;");
            writer.Outdent();
        }

        writer.Close();
        writer.Close();
        writer.Line();
        EmitConstruction(writer, plan, i => $"v{i}");
        writer.Close();
    }

    private static void EmitConstruction(CodeWriter writer, RowPlan plan, System.Func<int, string> value)
    {
        var members = plan.Members;
        var ctorArgs = new List<string>();
        var initializers = new List<string>();
        for (var i = 0; i < members.Count; i++)
        {
            switch (members[i].Kind)
            {
                case MemberKind.Constructor:
                    ctorArgs.Add(value(i));
                    break;
                case MemberKind.Initializer:
                    initializers.Add($"{members[i].Name} = {value(i)},");
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
            writer.Line($"result.{members[i].Name} = {value(i)};");
            writer.Close();
        }

        writer.Line("return result;");
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
