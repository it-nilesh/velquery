using System;

namespace VelQuery.Mapping;

/// <summary>
/// Strict, culture-invariant conversion of provider values to CLR types (VQ-FR-016, VQ-FR-029). Only lossless
/// conversions are performed: identical types, integral widening (range-checked narrowing), integral to
/// <see cref="decimal"/>/<see cref="double"/>, <see cref="float"/> to <see cref="double"/>, and integral to enum.
/// Strings are never parsed and <see cref="System.Convert.ChangeType(object, Type)"/> is never used.
/// </summary>
public static class ValueConverter
{
    /// <summary>Converts a provider value (possibly <see cref="DBNull"/>) to <typeparamref name="T"/>.</summary>
    public static T Convert<T>(object? value, string? queryId, string? column, bool strictNulls)
    {
        if (value is T typed)
        {
            return typed;
        }

        if (value is null || value is DBNull)
        {
            if (default(T) is null || !strictNulls)
            {
                return default!;
            }

            throw VelQueryMappingException.NullToNonNullable(queryId, column, typeof(T));
        }

        return (T)ConvertSlow(value, typeof(T), queryId, column, strictNulls)!;
    }

    /// <summary>
    /// Converts a provider value (possibly <see cref="DBNull"/>) to <paramref name="targetType"/>. When
    /// <paramref name="strictNulls"/> is false, NULL into a non-nullable value type returns <see langword="null"/>;
    /// reflection invocation treats that as the type's default value.
    /// </summary>
    public static object? Convert(object? value, Type targetType, string? queryId, string? column, bool strictNulls)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        if (value is not null && value is not DBNull && targetType.IsInstanceOfType(value))
        {
            return value;
        }

        return ConvertSlow(value, targetType, queryId, column, strictNulls);
    }

    private static object? ConvertSlow(object? value, Type targetType, string? queryId, string? column, bool strictNulls)
    {
        if (value is null || value is DBNull)
        {
            if (ScalarTypes.AcceptsNull(targetType))
            {
                return null;
            }

            if (strictNulls)
            {
                throw VelQueryMappingException.NullToNonNullable(queryId, column, targetType);
            }

            return null;
        }

        var target = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (target.IsInstanceOfType(value))
        {
            return value;
        }

        try
        {
            if (target.IsEnum)
            {
                if (IsIntegral(value))
                {
                    return Enum.ToObject(target, ToInt64OrUInt64(value));
                }
            }
            else if (IsIntegral(value))
            {
                switch (Type.GetTypeCode(target))
                {
                    case TypeCode.SByte: return checked((sbyte)ToInt128(value));
                    case TypeCode.Byte: return checked((byte)ToInt128(value));
                    case TypeCode.Int16: return checked((short)ToInt128(value));
                    case TypeCode.UInt16: return checked((ushort)ToInt128(value));
                    case TypeCode.Int32: return checked((int)ToInt128(value));
                    case TypeCode.UInt32: return checked((uint)ToInt128(value));
                    case TypeCode.Int64: return checked((long)ToInt128(value));
                    case TypeCode.UInt64: return checked((ulong)ToInt128(value));
                    case TypeCode.Decimal: return (decimal)ToInt128(value);
                    case TypeCode.Double when value is not long and not ulong: return (double)ToInt128(value);
                }
            }
            else if (value is float f && target == typeof(double))
            {
                return (double)f;
            }
        }
        catch (OverflowException ex)
        {
            throw VelQueryMappingException.Unconvertible(queryId, column, value.GetType(), targetType, ex);
        }

        throw VelQueryMappingException.Unconvertible(queryId, column, value.GetType(), targetType);
    }

    private static bool IsIntegral(object value)
        => value is sbyte or byte or short or ushort or int or uint or long or ulong;

    private static Int128 ToInt128(object value) => value switch
    {
        sbyte v => v,
        byte v => v,
        short v => v,
        ushort v => v,
        int v => v,
        uint v => v,
        long v => v,
        ulong v => v,
        _ => throw new InvalidCastException(),
    };

    private static object ToInt64OrUInt64(object value)
        => value is ulong u ? u : (object)(long)ToInt128(value);
}
