using System;
using System.Collections.Generic;
using System.ComponentModel;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace VelQuery.MongoDB.Serialization;

/// <summary>Reads one element value; the shape of every typed reader in <see cref="MongoBsonReading"/>.</summary>
public delegate T MongoValueReader<T>(IBsonReader reader, string? queryId, string? path, string field);

/// <summary>
/// Typed, strict value readers called by generated serializers once the reader is positioned on a value (its BSON type
/// has been read). Only lossless conversions are performed, mirroring <c>ValueConverter</c> (VQ-FR-016, VQ-FR-029):
/// Int32 widens to Int64, Double and Decimal; Int64 narrows to Int32 only when in range and widens to Decimal; Decimal128
/// converts to <see cref="decimal"/> only when exact. Double never converts to an integral or decimal type, and strings
/// are never parsed. BSON null (and the deprecated undefined) fails for non-nullable targets.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class MongoBsonReading
{
    /// <summary>Reads an <see cref="int"/>.</summary>
    public static int ReadInt32(IBsonReader reader, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var type = reader.CurrentBsonType;
        switch (type)
        {
            case BsonType.Int32:
                return reader.ReadInt32();
            case BsonType.Int64:
                var value = reader.ReadInt64();
                return value is >= int.MinValue and <= int.MaxValue
                    ? (int)value
                    : throw MongoMappingErrors.OutOfRange(queryId, path, field, type, typeof(int));
            default:
                throw Mismatch(type, queryId, path, field, typeof(int));
        }
    }

    /// <summary>Reads a nullable <see cref="int"/>.</summary>
    public static int? ReadNullableInt32(IBsonReader reader, string? queryId, string? path, string field)
        => TryReadNull(reader) ? null : ReadInt32(reader, queryId, path, field);

    /// <summary>Reads a <see cref="long"/>.</summary>
    public static long ReadInt64(IBsonReader reader, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var type = reader.CurrentBsonType;
        return type switch
        {
            BsonType.Int64 => reader.ReadInt64(),
            BsonType.Int32 => reader.ReadInt32(),
            _ => throw Mismatch(type, queryId, path, field, typeof(long)),
        };
    }

    /// <summary>Reads a nullable <see cref="long"/>.</summary>
    public static long? ReadNullableInt64(IBsonReader reader, string? queryId, string? path, string field)
        => TryReadNull(reader) ? null : ReadInt64(reader, queryId, path, field);

    /// <summary>Reads a <see cref="double"/>. Int64 is rejected: not every 64-bit integer is exact as a double.</summary>
    public static double ReadDouble(IBsonReader reader, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var type = reader.CurrentBsonType;
        return type switch
        {
            BsonType.Double => reader.ReadDouble(),
            BsonType.Int32 => reader.ReadInt32(),
            _ => throw Mismatch(type, queryId, path, field, typeof(double)),
        };
    }

    /// <summary>Reads a nullable <see cref="double"/>.</summary>
    public static double? ReadNullableDouble(IBsonReader reader, string? queryId, string? path, string field)
        => TryReadNull(reader) ? null : ReadDouble(reader, queryId, path, field);

    /// <summary>Reads a <see cref="decimal"/> from Decimal128 (exact values only), Int32 or Int64. Double is rejected.</summary>
    public static decimal ReadDecimal(IBsonReader reader, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var type = reader.CurrentBsonType;
        switch (type)
        {
            case BsonType.Decimal128:
                var raw = reader.ReadDecimal128();
                decimal result;
                try
                {
                    result = Decimal128.ToDecimal(raw);
                }
                catch (OverflowException ex)
                {
                    throw new VelQueryMappingException(queryId, MongoMappingErrors.Column(path, field), typeof(decimal),
                        $"the BSON {type} value is outside the range of 'Decimal'.", ex);
                }

                // Decimal128.ToDecimal rounds values with more than 28-29 significant digits; equality is numeric, so
                // trailing-zero differences (1.50 vs 1.5) still pass.
                return new Decimal128(result) == raw
                    ? result
                    : throw MongoMappingErrors.Inexact(queryId, path, field, type, typeof(decimal));
            case BsonType.Int32:
                return reader.ReadInt32();
            case BsonType.Int64:
                return reader.ReadInt64();
            default:
                throw Mismatch(type, queryId, path, field, typeof(decimal));
        }
    }

    /// <summary>Reads a nullable <see cref="decimal"/>.</summary>
    public static decimal? ReadNullableDecimal(IBsonReader reader, string? queryId, string? path, string field)
        => TryReadNull(reader) ? null : ReadDecimal(reader, queryId, path, field);

    /// <summary>Reads a <see cref="Decimal128"/> from Decimal128, Int32 or Int64.</summary>
    public static Decimal128 ReadDecimal128(IBsonReader reader, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var type = reader.CurrentBsonType;
        return type switch
        {
            BsonType.Decimal128 => reader.ReadDecimal128(),
            BsonType.Int32 => new Decimal128(reader.ReadInt32()),
            BsonType.Int64 => new Decimal128(reader.ReadInt64()),
            _ => throw Mismatch(type, queryId, path, field, typeof(Decimal128)),
        };
    }

    /// <summary>Reads a nullable <see cref="Decimal128"/>.</summary>
    public static Decimal128? ReadNullableDecimal128(IBsonReader reader, string? queryId, string? path, string field)
        => TryReadNull(reader) ? null : ReadDecimal128(reader, queryId, path, field);

    /// <summary>Reads a <see cref="bool"/>.</summary>
    public static bool ReadBoolean(IBsonReader reader, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var type = reader.CurrentBsonType;
        return type == BsonType.Boolean ? reader.ReadBoolean() : throw Mismatch(type, queryId, path, field, typeof(bool));
    }

    /// <summary>Reads a nullable <see cref="bool"/>.</summary>
    public static bool? ReadNullableBoolean(IBsonReader reader, string? queryId, string? path, string field)
        => TryReadNull(reader) ? null : ReadBoolean(reader, queryId, path, field);

    /// <summary>Reads a non-null <see cref="string"/> (BSON string or the deprecated symbol).</summary>
    public static string ReadString(IBsonReader reader, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var type = reader.CurrentBsonType;
        return type switch
        {
            BsonType.String => reader.ReadString(),
            BsonType.Symbol => reader.ReadSymbol(),
            _ => throw Mismatch(type, queryId, path, field, typeof(string)),
        };
    }

    /// <summary>Reads a nullable <see cref="string"/>.</summary>
    public static string? ReadNullableString(IBsonReader reader, string? queryId, string? path, string field)
        => TryReadNull(reader) ? null : ReadString(reader, queryId, path, field);

    /// <summary>Reads a <see cref="Guid"/> stored as binary subtype 4 (UuidStandard).</summary>
    public static Guid ReadGuid(IBsonReader reader, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var type = reader.CurrentBsonType;
        if (type != BsonType.Binary)
        {
            throw Mismatch(type, queryId, path, field, typeof(Guid));
        }

        var data = reader.ReadBinaryData();
        return data.SubType == BsonBinarySubType.UuidStandard
            ? data.ToGuid()
            : throw MongoMappingErrors.BinarySubType(queryId, path, field, data.SubType, typeof(Guid));
    }

    /// <summary>Reads a nullable <see cref="Guid"/>.</summary>
    public static Guid? ReadNullableGuid(IBsonReader reader, string? queryId, string? path, string field)
        => TryReadNull(reader) ? null : ReadGuid(reader, queryId, path, field);

    /// <summary>Reads a BSON date as a UTC <see cref="DateTime"/> (millisecond precision).</summary>
    public static DateTime ReadDateTime(IBsonReader reader, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var type = reader.CurrentBsonType;
        if (type != BsonType.DateTime)
        {
            throw Mismatch(type, queryId, path, field, typeof(DateTime));
        }

        var milliseconds = reader.ReadDateTime();
        if (milliseconds < BsonConstants.DateTimeMinValueMillisecondsSinceEpoch || milliseconds > BsonConstants.DateTimeMaxValueMillisecondsSinceEpoch)
        {
            throw MongoMappingErrors.OutOfRange(queryId, path, field, type, typeof(DateTime));
        }

        return BsonUtils.ToDateTimeFromMillisecondsSinceEpoch(milliseconds);
    }

    /// <summary>Reads a nullable <see cref="DateTime"/>.</summary>
    public static DateTime? ReadNullableDateTime(IBsonReader reader, string? queryId, string? path, string field)
        => TryReadNull(reader) ? null : ReadDateTime(reader, queryId, path, field);

    /// <summary>Reads an <see cref="ObjectId"/>.</summary>
    public static ObjectId ReadObjectId(IBsonReader reader, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var type = reader.CurrentBsonType;
        return type == BsonType.ObjectId ? reader.ReadObjectId() : throw Mismatch(type, queryId, path, field, typeof(ObjectId));
    }

    /// <summary>Reads a nullable <see cref="ObjectId"/>.</summary>
    public static ObjectId? ReadNullableObjectId(IBsonReader reader, string? queryId, string? path, string field)
        => TryReadNull(reader) ? null : ReadObjectId(reader, queryId, path, field);

    /// <summary>Reads non-null bytes from binary subtype 0 (generic) or 2 (old binary).</summary>
    public static byte[] ReadBytes(IBsonReader reader, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var type = reader.CurrentBsonType;
        if (type != BsonType.Binary)
        {
            throw Mismatch(type, queryId, path, field, typeof(byte[]));
        }

        // Subtype 2 is the deprecated "old binary" encoding of the same generic bytes.
        var data = reader.ReadBinaryData();
        return data.SubType is BsonBinarySubType.Binary or (BsonBinarySubType)2
            ? data.Bytes
            : throw MongoMappingErrors.BinarySubType(queryId, path, field, data.SubType, typeof(byte[]));
    }

    /// <summary>Reads nullable bytes.</summary>
    public static byte[]? ReadNullableBytes(IBsonReader reader, string? queryId, string? path, string field)
        => TryReadNull(reader) ? null : ReadBytes(reader, queryId, path, field);

    /// <summary>Reads an embedded document as a <see cref="BsonDocument"/> (schemaless escape hatch).</summary>
    public static BsonDocument ReadBsonDocument(IBsonReader reader, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var type = reader.CurrentBsonType;
        return type == BsonType.Document
            ? BsonDocumentSerializer.Instance.Deserialize(BsonDeserializationContext.CreateRoot(reader))
            : throw Mismatch(type, queryId, path, field, typeof(BsonDocument));
    }

    /// <summary>Reads a nullable embedded <see cref="BsonDocument"/>.</summary>
    public static BsonDocument? ReadNullableBsonDocument(IBsonReader reader, string? queryId, string? path, string field)
        => TryReadNull(reader) ? null : ReadBsonDocument(reader, queryId, path, field);

    /// <summary>Reads an embedded document with its generated serializer.</summary>
    public static T ReadDocument<T>(IBsonReader reader, VelQueryBsonSerializer<T> serializer, string? queryId, string? path, string field)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(serializer);
        var type = reader.CurrentBsonType;
        return type == BsonType.Document
            ? serializer.Read(reader, queryId, path is null ? field : path + "." + field)
            : throw Mismatch(type, queryId, path, field, typeof(T));
    }

    /// <summary>Reads a nullable embedded document with its generated serializer.</summary>
    public static T? ReadNullableDocument<T>(IBsonReader reader, VelQueryBsonSerializer<T> serializer, string? queryId, string? path, string field)
        where T : class
        => TryReadNull(reader) ? null : ReadDocument(reader, serializer, queryId, path, field);

    /// <summary>
    /// Reads a BSON array. <paramref name="elementField"/> (for example <c>tags[]</c>) names elements in errors.
    /// </summary>
    public static List<T> ReadList<T>(IBsonReader reader, string? queryId, string? path, string field, string elementField, MongoValueReader<T> element)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(element);
        var type = reader.CurrentBsonType;
        if (type != BsonType.Array)
        {
            throw Mismatch(type, queryId, path, field, typeof(List<T>));
        }

        var list = new List<T>();
        reader.ReadStartArray();
        while (reader.ReadBsonType() != BsonType.EndOfDocument)
        {
            list.Add(element(reader, queryId, path, elementField));
        }

        reader.ReadEndArray();
        return list;
    }

    /// <summary>Reads a nullable BSON array.</summary>
    public static List<T>? ReadNullableList<T>(IBsonReader reader, string? queryId, string? path, string field, string elementField, MongoValueReader<T> element)
        => TryReadNull(reader) ? null : ReadList(reader, queryId, path, field, elementField, element);

    /// <summary>Raised by generated serializers when a required field is absent.</summary>
    public static VelQueryMappingException Missing(string? queryId, string? path, string field, Type target)
        => MongoMappingErrors.Missing(queryId, path, field, target);

    /// <summary>Raised by generated serializers when a mapped field occurs twice.</summary>
    public static VelQueryMappingException Duplicate(string? queryId, string? path, string field, Type target)
        => MongoMappingErrors.Duplicate(queryId, path, field, target);

    private static bool TryReadNull(IBsonReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        switch (reader.CurrentBsonType)
        {
            case BsonType.Null:
                reader.ReadNull();
                return true;
            case BsonType.Undefined:
                reader.ReadUndefined();
                return true;
            default:
                return false;
        }
    }

    private static VelQueryMappingException Mismatch(BsonType type, string? queryId, string? path, string field, Type target)
        => type is BsonType.Null or BsonType.Undefined
            ? MongoMappingErrors.Null(queryId, path, field, target)
            : MongoMappingErrors.Unconvertible(queryId, path, field, type, target);
}
