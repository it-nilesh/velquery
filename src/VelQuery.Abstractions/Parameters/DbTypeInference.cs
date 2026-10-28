using System;
using System.Data;

namespace VelQuery.Parameters;

/// <summary>Infers <see cref="DbType"/> from CLR types without reflection.</summary>
public static class DbTypeInference
{
    /// <summary>Returns the default <see cref="DbType"/> for <paramref name="type"/>, or <see langword="null"/> if unknown.</summary>
    public static DbType? Infer(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum)
        {
            type = Enum.GetUnderlyingType(type);
        }

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => DbType.Boolean,
            TypeCode.Byte => DbType.Byte,
            TypeCode.SByte => DbType.SByte,
            TypeCode.Int16 => DbType.Int16,
            TypeCode.UInt16 => DbType.UInt16,
            TypeCode.Int32 => DbType.Int32,
            TypeCode.UInt32 => DbType.UInt32,
            TypeCode.Int64 => DbType.Int64,
            TypeCode.UInt64 => DbType.UInt64,
            TypeCode.Single => DbType.Single,
            TypeCode.Double => DbType.Double,
            TypeCode.Decimal => DbType.Decimal,
            TypeCode.String => DbType.String,
            TypeCode.Char => DbType.StringFixedLength,
            TypeCode.DateTime => DbType.DateTime2,
            _ when type == typeof(Guid) => DbType.Guid,
            _ when type == typeof(DateTimeOffset) => DbType.DateTimeOffset,
            _ when type == typeof(TimeSpan) => DbType.Time,
            _ when type == typeof(DateOnly) => DbType.Date,
            _ when type == typeof(TimeOnly) => DbType.Time,
            _ when type == typeof(byte[]) => DbType.Binary,
            _ when typeof(IProviderParameterValue).IsAssignableFrom(type) => DbType.Object,
            _ => null,
        };
    }
}
