using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using VelQuery.MongoDB.Generators.Model;

namespace VelQuery.MongoDB.Generators.Analysis;

/// <summary>Compile-time type rules shared by document plans and parameter binding.</summary>
internal static class MongoTypeFacts
{
    public const string SupportedScalars = "string, bool, int, long, double, decimal, Guid, DateTime, ObjectId, Decimal128, byte[] and int- or long-based enums";

    public static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static string Display(ITypeSymbol type) => type.ToDisplayString(FullyQualified);

    public static string DisplayNoAnnotations(ITypeSymbol type)
        => type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    public static ITypeSymbol? NullableUnderlying(ITypeSymbol type)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } named ? named.TypeArguments[0] : null;

    /// <summary>Whether a value of the declared type can be null (nullable-oblivious reference types count as nullable).</summary>
    public static bool AcceptsNull(ITypeSymbol type)
        => type.IsValueType ? NullableUnderlying(type) is not null : type.NullableAnnotation != NullableAnnotation.NotAnnotated;

    public static bool IsType(ITypeSymbol? type, string ns, string name, int arity = 0)
        => type is INamedTypeSymbol named
            && named.Name == name
            && named.Arity == arity
            && named.ContainingType is null
            && NamespaceIs(named.ContainingNamespace, ns);

    public static bool IsByteArray(ITypeSymbol type) => type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte };

    /// <summary>
    /// The scalar helper suffix for a (non-nullable) type, plus the enum type and its integral underlying type for
    /// enums; null when the type is not a supported scalar. <c>BsonDocument</c> is only valid for members.
    /// </summary>
    public static (string Scalar, string? EnumType, string? EnumUnderlying)? Scalar(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying } enumType)
        {
            return underlying.SpecialType switch
            {
                SpecialType.System_Int32 => ("Int32", DisplayNoAnnotations(enumType), "int"),
                SpecialType.System_Int64 => ("Int64", DisplayNoAnnotations(enumType), "long"),
                _ => null,
            };
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Int32: return ("Int32", null, null);
            case SpecialType.System_Int64: return ("Int64", null, null);
            case SpecialType.System_Double: return ("Double", null, null);
            case SpecialType.System_Decimal: return ("Decimal", null, null);
            case SpecialType.System_Boolean: return ("Boolean", null, null);
            case SpecialType.System_String: return ("String", null, null);
            case SpecialType.System_DateTime: return ("DateTime", null, null);
        }

        if (IsByteArray(type))
        {
            return ("Bytes", null, null);
        }

        if (IsType(type, "System", "Guid"))
        {
            return ("Guid", null, null);
        }

        if (IsType(type, "MongoDB.Bson", "ObjectId"))
        {
            return ("ObjectId", null, null);
        }

        if (IsType(type, "MongoDB.Bson", "Decimal128"))
        {
            return ("Decimal128", null, null);
        }

        if (IsType(type, "MongoDB.Bson", "BsonDocument"))
        {
            return ("BsonDocument", null, null);
        }

        return null;
    }

    /// <summary>The element type when <paramref name="type"/> is a sequence (array or <c>IEnumerable&lt;T&gt;</c>), excluding string and byte[].</summary>
    public static ITypeSymbol? SequenceElement(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String || IsByteArray(type))
        {
            return null;
        }

        if (type is IArrayTypeSymbol { Rank: 1 } array)
        {
            return array.ElementType;
        }

        if (type is INamedTypeSymbol named)
        {
            if (IsType(named, "System.Collections.Generic", "IEnumerable", 1))
            {
                return named.TypeArguments[0];
            }

            var enumerable = named.AllInterfaces.FirstOrDefault(i => IsType(i, "System.Collections.Generic", "IEnumerable", 1));
            return enumerable?.TypeArguments[0];
        }

        return null;
    }

    /// <summary>
    /// The shape of a contract parameter bound to a placeholder: a scalar, or a sequence of scalars. Returns the
    /// reason when the type cannot be bound. Documents and BSON values are rejected on purpose: a document argument
    /// could carry operators such as <c>$ne</c> into a filter.
    /// </summary>
    public static (ValueShape? Shape, string? Reason) ParameterShape(ITypeSymbol type)
    {
        var shape = ScalarShape(type, allowDocument: false);
        if (shape is not null)
        {
            return (shape, null);
        }

        if (IsBsonValue(type))
        {
            return (null, "BSON documents and values could carry query operators (for example { \"$ne\": null }) into the query, so only scalars and sequences of scalars can be bound; use typed parameters");
        }

        var element = SequenceElement(type);
        if (element is not null)
        {
            var elementShape = ScalarShape(element, allowDocument: false);
            if (elementShape is null)
            {
                return (null, $"sequence elements must be scalars ({SupportedScalars})");
            }

            return (new ValueShape(ShapeKind.List, string.Empty, Display(type), DisplayNoAnnotations(type), AcceptsNull(type), null, null, null, elementShape, false), null);
        }

        return (null, $"only scalars ({SupportedScalars}) and sequences of scalars can be bound");
    }

    /// <summary>A scalar shape for <paramref name="type"/> (possibly nullable), or null.</summary>
    public static ValueShape? ScalarShape(ITypeSymbol type, bool allowDocument)
    {
        var valueType = NullableUnderlying(type) ?? type;
        var scalar = Scalar(valueType);
        if (scalar is null || (!allowDocument && scalar.Value.Scalar == "BsonDocument"))
        {
            return null;
        }

        return new ValueShape(ShapeKind.Scalar, scalar.Value.Scalar, Display(type), BareTypeOf(type), AcceptsNull(type),
            scalar.Value.EnumType, scalar.Value.EnumUnderlying, null, null, false);
    }

    /// <summary>A type usable in <c>typeof</c>: nullable value types keep <c>?</c>, reference annotations are dropped.</summary>
    public static string BareTypeOf(ITypeSymbol type) => type.IsValueType ? Display(type) : DisplayNoAnnotations(type);

    public static bool IsBsonValue(ITypeSymbol type)
    {
        for (var t = (NullableUnderlying(type) ?? type) as INamedTypeSymbol; t is not null; t = t.BaseType)
        {
            if (IsType(t, "MongoDB.Bson", "BsonValue"))
            {
                return true;
            }
        }

        return type.SpecialType == SpecialType.System_Object || type.TypeKind == TypeKind.Dynamic;
    }

    /// <summary>Escapes C# keywords with <c>@</c>.</summary>
    public static string Identifier(string name)
        => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) == SyntaxKind.AwaitKeyword
            ? "@" + name
            : name;

    /// <summary>A C# string literal; non-printable and non-ASCII characters are escaped so output is ASCII-stable.</summary>
    public static string Literal(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < ' ' || c > '~')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    /// <summary>Injective mapping of a type name to an identifier: '_' doubles, other separators become '_'.</summary>
    public static string Sanitize(string display)
    {
        if (display.StartsWith("global::", System.StringComparison.Ordinal))
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

    private static bool NamespaceIs(INamespaceSymbol? symbol, string ns)
    {
        var parts = ns.Split('.');
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (symbol is null || symbol.Name != parts[i])
            {
                return false;
            }

            symbol = symbol.ContainingNamespace;
        }

        return symbol is { IsGlobalNamespace: true };
    }
}
