using System;

namespace VelQuery.Mapping;

/// <summary>Identifies CLR types that VelQuery maps directly from a single column.</summary>
public static class ScalarTypes
{
    /// <summary>Returns true when <paramref name="type"/> (or its nullable underlying type) is a single-column scalar.</summary>
    public static bool IsScalar(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(Guid)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan)
            || type == typeof(DateOnly)
            || type == typeof(TimeOnly)
            || type == typeof(byte[]);
    }

    /// <summary>Returns true when <paramref name="type"/> can hold <see langword="null"/>.</summary>
    public static bool AcceptsNull(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
    }
}
