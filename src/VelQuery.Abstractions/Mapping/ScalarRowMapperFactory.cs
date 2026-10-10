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
    public IRowMapper<T> Create(DbDataReader reader, in MappingContext context)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.FieldCount < 1)
        {
            throw new VelQueryMappingException(context.QueryId, null, typeof(T), "the result set has no columns.");
        }

        return new Mapper(reader.GetName(0), context);
    }

    private sealed class Mapper(string column, MappingContext context) : IRowMapper<T>
    {
        public T Map(DbDataReader reader)
            => ValueConverter.Convert<T>(reader.GetValue(0), context.QueryId, column, context.StrictNulls);
    }
}
