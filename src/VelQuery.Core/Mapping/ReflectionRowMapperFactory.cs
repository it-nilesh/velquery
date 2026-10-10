using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace VelQuery.Mapping;

/// <summary>Shared constants for the reflection fallback mapper.</summary>
public static class ReflectionRowMapperFactory
{
    /// <summary>Members the fallback mapper needs preserved by the trimmer.</summary>
    public const DynamicallyAccessedMemberTypes RequiredMembers =
        DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties;
}

/// <summary>
/// Opt-in compatibility mapper (VelQueryOptions.AllowDynamicFallback) using reflection over annotated public
/// constructors and properties. It invokes members but never emits code. Rules mirror the generated mapper:
/// <list type="bullet">
/// <item>Column names match members via <see cref="ColumnAttribute"/> or the member name, case-insensitively.</item>
/// <item>A public parameterless constructor plus settable properties is preferred; otherwise the single widest public
/// constructor is used (records) and every one of its parameters must match a column. Ties are rejected.</item>
/// <item>Constructor parameters and <c>required</c> properties must be present; extra columns are ignored.</item>
/// <item>Two result columns matching the same member are rejected as ambiguous (VQ-FR-015).</item>
/// <item>NULL into a non-nullable value type, or into a reference type annotated non-nullable, fails in strict mode.</item>
/// </list>
/// </summary>
internal sealed class ReflectionRowMapperFactory<[DynamicallyAccessedMembers(ReflectionRowMapperFactory.RequiredMembers)] T> : IRowMapperFactory<T>
{
    public static readonly ReflectionRowMapperFactory<T> Instance = new();

    private readonly Plan _plan = BuildPlan();

    public IRowMapper<T> Create(DbDataReader reader, in MappingContext mappingContext)
    {
        var context = mappingContext;
        if (ScalarTypes.IsScalar(typeof(T)))
        {
            return ScalarRowMapperFactory<T>.Instance.Create(reader, context);
        }

        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (!columns.TryAdd(name, i))
            {
                duplicates.Add(name);
            }
        }

        int Ordinal(MemberPlan member, bool required)
        {
            if (duplicates.Contains(member.Column))
            {
                throw new VelQueryMappingException(context.QueryId, member.Column, member.Type,
                    "the result contains more than one column with this name; alias the columns or declare an explicit mapping.");
            }

            if (columns.TryGetValue(member.Column, out var ordinal))
            {
                return ordinal;
            }

            return required ? throw VelQueryMappingException.MissingColumn(context.QueryId, member.Column, member.Type) : -1;
        }

        Plan plan = _plan;
        if (plan.Constructor is null)
        {
            throw new VelQueryMappingException(context.QueryId, null, typeof(T),
                "the type needs a public parameterless constructor or exactly one widest public constructor.");
        }

        var ctorOrdinals = plan.ConstructorParameters.Select(p => Ordinal(p, required: true)).ToArray();
        var propertyOrdinals = plan.Properties.Select(p => Ordinal(p, p.Required)).ToArray();
        return new Mapper(plan, ctorOrdinals, propertyOrdinals, context);
    }

    private static Plan BuildPlan()
    {
        if (ScalarTypes.IsScalar(typeof(T)))
        {
            return new Plan(null, [], []);
        }

        var nullability = new NullabilityInfoContext();
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .ToArray();

        string ColumnName(PropertyInfo p) => p.GetCustomAttribute<ColumnAttribute>()?.Name ?? p.Name;

        var constructors = typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var parameterless = constructors.FirstOrDefault(c => c.GetParameters().Length == 0);
        ConstructorInfo? constructor = parameterless;
        MemberPlan[] ctorMembers = [];
        if (parameterless is null)
        {
            // Same rule as the generator (VQ1003): the widest public constructor, rejecting ties rather than guessing.
            var widest = constructors.Length == 0 ? 0 : constructors.Max(c => c.GetParameters().Length);
            var candidates = constructors.Where(c => c.GetParameters().Length == widest).ToArray();
            constructor = candidates.Length == 1 ? candidates[0] : null;
            if (constructor is not null)
            {
                ctorMembers = constructor.GetParameters().Select(parameter =>
                {
                    var property = properties.FirstOrDefault(p => string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
                    var column = property is null ? parameter.Name! : ColumnName(property);
                    var nullable = !parameter.ParameterType.IsValueType
                        ? nullability.Create(parameter).WriteState != NullabilityState.NotNull
                        : Nullable.GetUnderlyingType(parameter.ParameterType) is not null;
                    return new MemberPlan(column, parameter.ParameterType, nullable, Required: true, Property: null);
                }).ToArray();
            }
        }

        var bound = new HashSet<string>(ctorMembers.Select(m => m.Column), StringComparer.OrdinalIgnoreCase);
        var settable = properties
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => new MemberPlan(
                ColumnName(p),
                p.PropertyType,
                !p.PropertyType.IsValueType
                    ? nullability.Create(p).WriteState != NullabilityState.NotNull
                    : Nullable.GetUnderlyingType(p.PropertyType) is not null,
                Required: p.IsDefined(typeof(RequiredMemberAttribute)),
                Property: p))
            .Where(m => !bound.Contains(m.Column))
            .ToArray();

        return new Plan(constructor, ctorMembers, settable);
    }

    private sealed record Plan(ConstructorInfo? Constructor, MemberPlan[] ConstructorParameters, MemberPlan[] Properties);

    private sealed record MemberPlan(string Column, Type Type, bool Nullable, bool Required, PropertyInfo? Property);

    private sealed class Mapper(Plan plan, int[] ctorOrdinals, int[] propertyOrdinals, MappingContext context) : IRowMapper<T>
    {
        public T Map(DbDataReader reader)
        {
            var args = new object?[ctorOrdinals.Length];
            for (var i = 0; i < args.Length; i++)
            {
                args[i] = Read(reader, ctorOrdinals[i], plan.ConstructorParameters[i]);
            }

            var instance = plan.Constructor!.Invoke(args);
            for (var i = 0; i < propertyOrdinals.Length; i++)
            {
                if (propertyOrdinals[i] >= 0)
                {
                    var member = plan.Properties[i];
                    member.Property!.SetValue(instance, Read(reader, propertyOrdinals[i], member));
                }
            }

            return (T)instance;
        }

        private object? Read(DbDataReader reader, int ordinal, MemberPlan member)
        {
            var value = reader.GetValue(ordinal);
            if (value is DBNull && !member.Nullable && context.StrictNulls)
            {
                throw VelQueryMappingException.NullToNonNullable(context.QueryId, member.Column, member.Type);
            }

            return ValueConverter.Convert(value, member.Type, context.QueryId, member.Column, context.StrictNulls && !member.Nullable);
        }
    }
}
