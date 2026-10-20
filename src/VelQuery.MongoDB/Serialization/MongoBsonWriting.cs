using System;
using System.Collections.Generic;
using System.ComponentModel;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace VelQuery.MongoDB.Serialization;

/// <summary>Writes one value (the element name, if any, has been written); the shape of every writer in <see cref="MongoBsonWriting"/>.</summary>
public delegate void MongoValueWriter<T>(IBsonWriter writer, T value, string? queryId, string? path, string field);

/// <summary>
/// Typed value writers called by generated serializers. Writes are lossless except where BSON itself is coarser, which
/// is documented: dates are stored with millisecond precision (sub-millisecond ticks are truncated, as the driver does),
/// <see cref="decimal"/> is stored as Decimal128, Guids as binary subtype 4 and enums as their integral value.
/// A <see cref="DateTime"/> with <see cref="DateTimeKind.Unspecified"/> is rejected rather than guessed, and a null in a
/// member declared non-nullable is rejected so every written document can be read back in strict mode.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class MongoBsonWriting
{
    /// <summary>Writes an <see cref="int"/>.</summary>
    public static void WriteInt32(IBsonWriter writer, int value, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(value);
    }

    /// <summary>Writes a nullable <see cref="int"/>.</summary>
    public static void WriteNullableInt32(IBsonWriter writer, int? value, string? queryId, string? path, string field)
    {
        if (value is { } v)
        {
            WriteInt32(writer, v, queryId, path, field);
        }
        else
        {
            WriteNull(writer);
        }
    }

    /// <summary>Writes a <see cref="long"/>.</summary>
    public static void WriteInt64(IBsonWriter writer, long value, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt64(value);
    }

    /// <summary>Writes a nullable <see cref="long"/>.</summary>
    public static void WriteNullableInt64(IBsonWriter writer, long? value, string? queryId, string? path, string field)
    {
        if (value is { } v)
        {
            WriteInt64(writer, v, queryId, path, field);
        }
        else
        {
            WriteNull(writer);
        }
    }

    /// <summary>Writes a <see cref="double"/>.</summary>
    public static void WriteDouble(IBsonWriter writer, double value, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteDouble(value);
    }

    /// <summary>Writes a nullable <see cref="double"/>.</summary>
    public static void WriteNullableDouble(IBsonWriter writer, double? value, string? queryId, string? path, string field)
    {
        if (value is { } v)
        {
            WriteDouble(writer, v, queryId, path, field);
        }
        else
        {
            WriteNull(writer);
        }
    }

    /// <summary>Writes a <see cref="decimal"/> as Decimal128 (exact).</summary>
    public static void WriteDecimal(IBsonWriter writer, decimal value, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteDecimal128(new Decimal128(value));
    }

    /// <summary>Writes a nullable <see cref="decimal"/>.</summary>
    public static void WriteNullableDecimal(IBsonWriter writer, decimal? value, string? queryId, string? path, string field)
    {
        if (value is { } v)
        {
            WriteDecimal(writer, v, queryId, path, field);
        }
        else
        {
            WriteNull(writer);
        }
    }

    /// <summary>Writes a <see cref="Decimal128"/>.</summary>
    public static void WriteDecimal128(IBsonWriter writer, Decimal128 value, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteDecimal128(value);
    }

    /// <summary>Writes a nullable <see cref="Decimal128"/>.</summary>
    public static void WriteNullableDecimal128(IBsonWriter writer, Decimal128? value, string? queryId, string? path, string field)
    {
        if (value is { } v)
        {
            WriteDecimal128(writer, v, queryId, path, field);
        }
        else
        {
            WriteNull(writer);
        }
    }

    /// <summary>Writes a <see cref="bool"/>.</summary>
    public static void WriteBoolean(IBsonWriter writer, bool value, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBoolean(value);
    }

    /// <summary>Writes a nullable <see cref="bool"/>.</summary>
    public static void WriteNullableBoolean(IBsonWriter writer, bool? value, string? queryId, string? path, string field)
    {
        if (value is { } v)
        {
            WriteBoolean(writer, v, queryId, path, field);
        }
        else
        {
            WriteNull(writer);
        }
    }

    /// <summary>Writes a <see cref="string"/> declared non-nullable; null is rejected.</summary>
    public static void WriteString(IBsonWriter writer, string value, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null)
        {
            throw MongoMappingErrors.NullMember(queryId, path, field, typeof(string));
        }

        writer.WriteString(value);
    }

    /// <summary>Writes a nullable <see cref="string"/>.</summary>
    public static void WriteNullableString(IBsonWriter writer, string? value, string? queryId, string? path, string field)
    {
        if (value is null)
        {
            WriteNull(writer);
        }
        else
        {
            WriteString(writer, value, queryId, path, field);
        }
    }

    /// <summary>Writes a <see cref="Guid"/> as binary subtype 4 (UuidStandard).</summary>
    public static void WriteGuid(IBsonWriter writer, Guid value, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBinaryData(new BsonBinaryData(value, GuidRepresentation.Standard));
    }

    /// <summary>Writes a nullable <see cref="Guid"/>.</summary>
    public static void WriteNullableGuid(IBsonWriter writer, Guid? value, string? queryId, string? path, string field)
    {
        if (value is { } v)
        {
            WriteGuid(writer, v, queryId, path, field);
        }
        else
        {
            WriteNull(writer);
        }
    }

    /// <summary>Writes a <see cref="DateTime"/> as a BSON date. Local times are converted to UTC; Unspecified is rejected.</summary>
    public static void WriteDateTime(IBsonWriter writer, DateTime value, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteDateTime(BsonUtils.ToMillisecondsSinceEpoch(ToUtc(value, queryId, path, field)));
    }

    /// <summary>Writes a nullable <see cref="DateTime"/>.</summary>
    public static void WriteNullableDateTime(IBsonWriter writer, DateTime? value, string? queryId, string? path, string field)
    {
        if (value is { } v)
        {
            WriteDateTime(writer, v, queryId, path, field);
        }
        else
        {
            WriteNull(writer);
        }
    }

    /// <summary>Writes an <see cref="ObjectId"/>.</summary>
    public static void WriteObjectId(IBsonWriter writer, ObjectId value, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteObjectId(value);
    }

    /// <summary>Writes a nullable <see cref="ObjectId"/>.</summary>
    public static void WriteNullableObjectId(IBsonWriter writer, ObjectId? value, string? queryId, string? path, string field)
    {
        if (value is { } v)
        {
            WriteObjectId(writer, v, queryId, path, field);
        }
        else
        {
            WriteNull(writer);
        }
    }

    /// <summary>Writes bytes declared non-nullable as binary subtype 0.</summary>
    public static void WriteBytes(IBsonWriter writer, byte[] value, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null)
        {
            throw MongoMappingErrors.NullMember(queryId, path, field, typeof(byte[]));
        }

        writer.WriteBytes(value);
    }

    /// <summary>Writes nullable bytes.</summary>
    public static void WriteNullableBytes(IBsonWriter writer, byte[]? value, string? queryId, string? path, string field)
    {
        if (value is null)
        {
            WriteNull(writer);
        }
        else
        {
            WriteBytes(writer, value, queryId, path, field);
        }
    }

    /// <summary>Writes a <see cref="BsonDocument"/> declared non-nullable.</summary>
    public static void WriteBsonDocument(IBsonWriter writer, BsonDocument value, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null)
        {
            throw MongoMappingErrors.NullMember(queryId, path, field, typeof(BsonDocument));
        }

        BsonDocumentSerializer.Instance.Serialize(BsonSerializationContext.CreateRoot(writer), value);
    }

    /// <summary>Writes a nullable <see cref="BsonDocument"/>.</summary>
    public static void WriteNullableBsonDocument(IBsonWriter writer, BsonDocument? value, string? queryId, string? path, string field)
    {
        if (value is null)
        {
            WriteNull(writer);
        }
        else
        {
            WriteBsonDocument(writer, value, queryId, path, field);
        }
    }

    /// <summary>Writes an embedded document declared non-nullable with its generated serializer.</summary>
    public static void WriteDocument<T>(IBsonWriter writer, VelQueryBsonSerializer<T> serializer, T value, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(serializer);
        if (value is null)
        {
            throw MongoMappingErrors.NullMember(queryId, path, field, typeof(T));
        }

        serializer.Write(writer, value, queryId, path is null ? field : path + "." + field);
    }

    /// <summary>Writes a nullable embedded document with its generated serializer.</summary>
    public static void WriteNullableDocument<T>(IBsonWriter writer, VelQueryBsonSerializer<T> serializer, T? value, string? queryId, string? path, string field)
        where T : class
    {
        if (value is null)
        {
            WriteNull(writer);
        }
        else
        {
            WriteDocument(writer, serializer, value, queryId, path, field);
        }
    }

    /// <summary>Writes a sequence declared non-nullable as a BSON array.</summary>
    public static void WriteList<T>(IBsonWriter writer, IEnumerable<T> values, string? queryId, string? path, string field, string elementField, MongoValueWriter<T> element)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(element);
        if (values is null)
        {
            throw MongoMappingErrors.NullMember(queryId, path, field, typeof(IEnumerable<T>));
        }

        writer.WriteStartArray();
        foreach (var value in values)
        {
            element(writer, value, queryId, path, elementField);
        }

        writer.WriteEndArray();
    }

    /// <summary>Writes a nullable sequence as a BSON array.</summary>
    public static void WriteNullableList<T>(IBsonWriter writer, IEnumerable<T>? values, string? queryId, string? path, string field, string elementField, MongoValueWriter<T> element)
    {
        if (values is null)
        {
            WriteNull(writer);
        }
        else
        {
            WriteList(writer, values, queryId, path, field, elementField, element);
        }
    }

    internal static DateTime ToUtc(DateTime value, string? queryId, string? path, string field) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => throw MongoMappingErrors.UnspecifiedDateTime(queryId, path, field),
    };

    private static void WriteNull(IBsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNull();
    }
}
