using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using VelQuery.Generators.Diagnostics;
using VelQuery.Generators.Infrastructure;
using VelQuery.Generators.Model;

namespace VelQuery.Generators.Analysis;

/// <summary>
/// Builds the compile-time mapping plan for a non-scalar row type (PRD §9.3). The rules mirror the runtime reflection
/// fallback so both paths agree:
/// <list type="bullet">
/// <item>Columns match members by <c>[Column]</c> name or member name, case-insensitively.</item>
/// <item>A public parameterless constructor plus settable properties is preferred; otherwise the public constructor
/// with the most parameters (for example a record's primary constructor) is used. Its parameters are required.</item>
/// <item><c>required</c> and <c>init</c>-only properties are assigned in the object initializer and their columns are
/// required (an initializer cannot be conditional); other settable properties are assigned only when present.</item>
/// <item><c>[NotMapped]</c> properties and get-only properties not bound by the constructor are ignored.</item>
/// <item>Two members mapping to the same column are rejected (VQ1004).</item>
/// </list>
/// </summary>
internal static class RowPlanBuilder
{
    private const string SchemaNamespace = "System.ComponentModel.DataAnnotations.Schema";

    public static RowPlan? Build(ITypeSymbol rowType, LocationInfo? location, List<DiagnosticInfo> diagnostics)
    {
        var display = TypeFacts.DisplayNoAnnotations(rowType);
        void Reject(string reason)
            => diagnostics.Add(DiagnosticInfo.Create(Descriptors.UnsupportedRowType, location, display, reason));

        if (rowType.TypeKind == TypeKind.Error)
        {
            return null; // The compiler already reports the unresolved type.
        }

        if (rowType is not INamedTypeSymbol type || type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            Reject("only classes, records and structs can be mapped from rows");
            return null;
        }

        if (TypeFacts.NullableUnderlying(type) is not null)
        {
            Reject("nullable struct row types are not supported; use a class or record, or a non-nullable result");
            return null;
        }

        if (type.IsAbstract || type.IsStatic)
        {
            Reject("the type is abstract or static");
            return null;
        }

        if (type.IsGenericType)
        {
            Reject("generic row types are not supported");
            return null;
        }

        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
        {
            if (t.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                Reject("the type must be public or internal so generated code can construct it");
                return null;
            }
        }

        var properties = CollectProperties(type);
        var constructors = type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToList();
        var parameterless = constructors.FirstOrDefault(c => c.Parameters.Length == 0);
        IMethodSymbol? constructor = parameterless;
        if (constructor is null)
        {
            var max = constructors.Count == 0 ? -1 : constructors.Max(c => c.Parameters.Length);
            var candidates = constructors.Where(c => c.Parameters.Length == max).ToList();
            if (candidates.Count == 0)
            {
                Reject("it has no public constructor");
                return null;
            }

            if (candidates.Count > 1)
            {
                Reject($"it has {candidates.Count} public constructors with {max} parameters; the constructor to use is ambiguous");
                return null;
            }

            constructor = candidates[0];
        }

        var members = new List<RowMemberModel>();
        var origins = new List<string>();
        var boundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in constructor.Parameters)
        {
            if (parameter.RefKind is not (RefKind.None or RefKind.In) || parameter.IsParams)
            {
                Reject($"constructor parameter '{parameter.Name}' is ref, out or params");
                return null;
            }

            var property = properties.FirstOrDefault(p => string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
            var column = ColumnName(property) ?? ColumnName(parameter) ?? parameter.Name;
            var member = CreateMember(TypeFacts.Identifier(parameter.Name), column, parameter.Type, MemberKind.Constructor);
            if (member is null)
            {
                Reject($"constructor parameter '{parameter.Name}' has unsupported type '{TypeFacts.Display(parameter.Type)}'; only scalar member types can be mapped");
                return null;
            }

            members.Add(member);
            origins.Add(parameter.Name);
            boundNames.Add(parameter.Name);
        }

        foreach (var property in properties)
        {
            if (boundNames.Contains(property.Name)
                || property.SetMethod is not { DeclaredAccessibility: Accessibility.Public } setter
                || HasAttribute(property, "NotMappedAttribute"))
            {
                continue;
            }

            var kind = property.IsRequired || setter.IsInitOnly ? MemberKind.Initializer : MemberKind.Optional;
            var member = CreateMember(TypeFacts.Identifier(property.Name), ColumnName(property) ?? property.Name, property.Type, kind);
            if (member is null)
            {
                Reject($"property '{property.Name}' has unsupported type '{TypeFacts.Display(property.Type)}'; only scalar member types can be mapped (mark it [NotMapped] to skip it)");
                return null;
            }

            members.Add(member);
            origins.Add(property.Name);
        }

        if (members.Count == 0)
        {
            Reject("it has no constructor parameters or settable public properties to map");
            return null;
        }

        var ambiguous = false;
        for (var i = 0; i < members.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (string.Equals(members[i].Column, members[j].Column, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(DiagnosticInfo.Create(Descriptors.AmbiguousMapping, location, display, origins[j], origins[i], members[i].Column));
                    ambiguous = true;
                }
            }
        }

        if (ambiguous)
        {
            return null;
        }

        var factoryName = "RowMapperFactory_" + Sanitize(display);
        return new RowPlan(display, factoryName, "VelQuery.Generated." + factoryName + ".g.cs", members.ToEquatableArray());
    }

    private static RowMemberModel? CreateMember(string name, string column, ITypeSymbol type, MemberKind kind)
    {
        if (!TypeFacts.IsScalar(type))
        {
            return null;
        }

        var valueType = TypeFacts.NullableUnderlying(type) ?? type;
        string? enumCast = null;
        var readType = valueType;
        if (valueType is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying })
        {
            enumCast = TypeFacts.DisplayNoAnnotations(valueType);
            readType = underlying;
        }

        var (getter, fast) = TypeFacts.ReaderGetter(readType);
        return new RowMemberModel(
            name,
            column,
            TypeFacts.Display(type),
            TypeFacts.DisplayNoAnnotations(valueType),
            getter,
            new EquatableArray<string>(fast),
            enumCast,
            TypeFacts.AcceptsNull(type),
            kind);
    }

    /// <summary>Public instance non-indexer properties, most-derived declaration first, in declaration order.</summary>
    private static List<IPropertySymbol> CollectProperties(INamedTypeSymbol type)
    {
        var result = new List<IPropertySymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public
                    || property.Name == "EqualityContract" || !seen.Add(property.Name))
                {
                    continue;
                }

                result.Add(property);
            }

            if (current.TypeKind == TypeKind.Struct)
            {
                break;
            }
        }

        return result;
    }

    private static string? ColumnName(ISymbol? symbol)
    {
        if (symbol is null)
        {
            return null;
        }

        foreach (var attribute in symbol.GetAttributes())
        {
            if (TypeFacts.IsType(attribute.AttributeClass, SchemaNamespace, "ColumnAttribute")
                && attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is string { Length: > 0 } name)
            {
                return name;
            }
        }

        return null;
    }

    private static bool HasAttribute(ISymbol symbol, string name)
        => symbol.GetAttributes().Any(a => TypeFacts.IsType(a.AttributeClass, SchemaNamespace, name));

    /// <summary>Injective mapping of a type name to an identifier: '_' doubles, other separators become '_'.</summary>
    private static string Sanitize(string display)
    {
        if (display.StartsWith("global::", StringComparison.Ordinal))
        {
            display = display.Substring("global::".Length);
        }

        var builder = new StringBuilder(display.Length);
        foreach (var c in display)
        {
            if (c == '_')
            {
                builder.Append("__");
            }
            else if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('_');
            }
        }

        return builder.ToString();
    }
}
