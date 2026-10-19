using System;
using System.Data.Common;

namespace VelQuery.Mapping;

/// <summary>Maps the first column of each row to a scalar <typeparamref name="T"/>.</summary>
public sealed class ScalarRowMapperFactory<T> : IRowMapperFactory<T>
{
    /// <summary>Whether <typeparamref name="T"/> is a supported scalar type.</summary>
    public static readonly bool IsSupported = ScalarTypes.IsScalar(typeof(T));

    /// <summary>Shared instance.</summary>
    public static readonly ScalarRowMapperFactory<T> Instance = new();

    private ScalarRowMapperFactory()
    {
    }

    /// <inheritdoc />
    public bool SupportsSequentialAccess => true;

    /// <inheritdoc />
    public IRowMapper<T> Create(DbDataReader reader, in MappingContext context)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.FieldCount < 1)
        {
            throw new VelQueryMappingException(context.QueryId, null, typeof(T), "the result set has no columns.");
        }

        // When the provider column type is exactly T, read it with the typed getter: no boxing per row.
        return Nullable.GetUnderlyingType(typeof(T)) is null && reader.GetFieldType(0) == typeof(T)
            ? new TypedMapper(reader.GetName(0), context)
            : new ConvertingMapper(reader.GetName(0), context);
    }

    private sealed class TypedMapper(string column, MappingContext context) : IRowMapper<T>
    {
        public T Map(DbDataReader reader)
        {
            if (reader.IsDBNull(0))
            {
                return ValueConverter.Convert<T>(DBNull.Value, context.QueryId, column, context.StrictNulls);
            }

            return reader.GetFieldValue<T>(0);
        }
    }

    private sealed class ConvertingMapper(string column, MappingContext context) : IRowMapper<T>
    {
        // Drivers cannot produce Nullable<enum> from GetFieldValue (they unbox the stored integer); the lossless
        // integral-to-enum rule in ValueConverter handles it instead.
        private static readonly bool s_nullableEnum = Nullable.GetUnderlyingType(typeof(T)) is { IsEnum: true };

        public T Map(DbDataReader reader)
        {
            if (context.NativeConversions && !s_nullableEnum && !reader.IsDBNull(0))
            {
                return reader.GetFieldValue<T>(0);
            }

            return ValueConverter.Convert<T>(reader.GetValue(0), context.QueryId, column, context.StrictNulls);
        }
    }
}
