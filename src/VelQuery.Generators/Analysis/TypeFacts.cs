using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace VelQuery.Generators.Analysis;

/// <summary>Compile-time equivalents of the runtime <c>ScalarTypes</c> and <c>DbTypeInference</c> rules.</summary>
internal static class TypeFacts
{
    /// <summary>Fully qualified type names including nullable reference annotations.</summary>
    public static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>Fully qualified type names without nullable reference annotations.</summary>
    public static readonly SymbolDisplayFormat FullyQualifiedNoAnnotations = SymbolDisplayFormat.FullyQualifiedFormat;

    public static string Display(ITypeSymbol type) => type.ToDisplayString(FullyQualified);

    public static string DisplayNoAnnotations(ITypeSymbol type)
        => type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(FullyQualifiedNoAnnotations);

    /// <summary>Returns the underlying type of <c>Nullable&lt;T&gt;</c>, or null.</summary>
    public static ITypeSymbol? NullableUnderlying(ITypeSymbol type)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } named
            ? named.TypeArguments[0]
            : null;

    /// <summary>Mirrors <c>VelQuery.Mapping.ScalarTypes.IsScalar</c>.</summary>
    public static bool IsScalar(ITypeSymbol type)
    {
        type = NullableUnderlying(type) ?? type;
        if (type.TypeKind == TypeKind.Enum)
        {
            return true;
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Char:
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_String:
            case SpecialType.System_DateTime:
            case SpecialType.System_IntPtr:
            case SpecialType.System_UIntPtr:
                return true;
        }

        if (IsByteArray(type))
        {
            return true;
        }

        return WellKnownName(type) is "Guid" or "DateTimeOffset" or "TimeSpan" or "DateOnly" or "TimeOnly";
    }

    /// <summary>Whether a value of <paramref name="type"/> (as declared) can be null.</summary>
    public static bool AcceptsNull(ITypeSymbol type)
        => type.IsValueType
            ? NullableUnderlying(type) is not null
            : type.NullableAnnotation != NullableAnnotation.NotAnnotated; // oblivious treated as nullable, like reflection

    /// <summary>Mirrors <c>VelQuery.Parameters.DbTypeInference.Infer</c>; returns a <c>DbType</c> member name or null.</summary>
    public static string? InferDbType(ITypeSymbol type)
    {
        type = NullableUnderlying(type) ?? type;
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying })
        {
            type = underlying;
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return "Boolean";
            case SpecialType.System_Byte: return "Byte";
            case SpecialType.System_SByte: return "SByte";
            case SpecialType.System_Int16: return "Int16";
            case SpecialType.System_UInt16: return "UInt16";
            case SpecialType.System_Int32: return "Int32";
            case SpecialType.System_UInt32: return "UInt32";
            case SpecialType.System_Int64: return "Int64";
            case SpecialType.System_UInt64: return "UInt64";
            case SpecialType.System_Single: return "Single";
            case SpecialType.System_Double: return "Double";
            case SpecialType.System_Decimal: return "Decimal";
            case SpecialType.System_String: return "String";
            case SpecialType.System_Char: return "StringFixedLength";
            case SpecialType.System_DateTime: return "DateTime2";
        }

        if (IsByteArray(type))
        {
            return "Binary";
        }

        return WellKnownName(type) switch
        {
            "Guid" => "Guid",
            "DateTimeOffset" => "DateTimeOffset",
            "TimeSpan" => "Time",
            "DateOnly" => "Date",
            "TimeOnly" => "Time",
            _ => null,
        };
    }

    /// <summary>
    /// Selects the typed <c>DbDataReader</c> getter for a non-nullable scalar <paramref name="type"/>, and the provider
    /// field types for which it may be called directly. Other field types go through <c>ValueConverter</c>.
    /// </summary>
    public static (string Getter, string[] FastTypes) ReaderGetter(ITypeSymbol type)
    {
        var display = DisplayNoAnnotations(type);
        var getter = type.SpecialType switch
        {
            SpecialType.System_Boolean => "GetBoolean",
            SpecialType.System_Byte => "GetByte",
            SpecialType.System_Int16 => "GetInt16",
            SpecialType.System_Int32 => "GetInt32",
            SpecialType.System_Int64 => "GetInt64",
            SpecialType.System_Single => "GetFloat",
            SpecialType.System_Double => "GetDouble",
            SpecialType.System_Decimal => "GetDecimal",
            SpecialType.System_String => "GetString",
            SpecialType.System_Char => "GetChar",
            SpecialType.System_DateTime => "GetDateTime",
            _ when WellKnownName(type) == "Guid" => "GetGuid",
            _ => "GetFieldValue<" + display + ">",
        };

        // Drivers report DATE/TIME columns as DateTime/TimeSpan but convert natively via GetFieldValue<T>.
        var fast = WellKnownName(type) switch
        {
            "DateOnly" => new[] { display, "global::System.DateTime" },
            "TimeOnly" => new[] { display, "global::System.TimeSpan" },
            _ => new[] { display },
        };

        return (getter, fast);
    }

    public static bool IsByteArray(ITypeSymbol type)
        => type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte };

    /// <summary>Simple name of a well-known <c>System</c> value type, or null.</summary>
    private static string? WellKnownName(ITypeSymbol type)
        => type is INamedTypeSymbol { ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true }, Arity: 0 } named
            ? named.Name
            : null;

    public static bool IsType(ITypeSymbol? type, string ns, string name, int arity = 0)
        => type is INamedTypeSymbol named
            && named.Name == name
            && named.Arity == arity
            && NamespaceIs(named.ContainingNamespace, ns)
            && named.ContainingType is null;

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

    /// <summary>Escapes C# keywords with <c>@</c>.</summary>
    public static string Identifier(string name)
        => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) == SyntaxKind.AwaitKeyword
            ? "@" + name
            : name;
}
