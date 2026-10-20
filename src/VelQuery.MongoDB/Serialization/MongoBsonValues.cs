using System;
using System.Collections.Generic;
using System.ComponentModel;
using MongoDB.Bson;

namespace VelQuery.MongoDB.Serialization;

/// <summary>
/// Converts typed contract arguments into BSON <em>values</em> for generated filters, updates and pipelines. Every
/// overload produces a scalar or an array of scalars; none can produce a document, so an argument can never become a
/// query operator such as <c>$where</c> or <c>$ne</c> (the NoSQL-injection guarantee, ADR-0019). Strings stay strings
/// whatever they contain. Conversions follow <see cref="MongoBsonWriting"/>: decimals as Decimal128, Guids as binary
/// subtype 4, dates as UTC instants (Unspecified kind rejected), enums as their integral value.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class MongoBsonValues
{
    /// <summary>Converts a string (null becomes BSON null).</summary>
    public static BsonValue From(string? value) => value is null ? BsonNull.Value : new BsonString(value);

    /// <summary>Converts an <see cref="int"/>.</summary>
    public static BsonValue From(int value) => (BsonInt32)value;

    /// <summary>Converts a nullable <see cref="int"/>.</summary>
    public static BsonValue From(int? value) => value is { } v ? (BsonInt32)v : BsonNull.Value;

    /// <summary>Converts a <see cref="long"/>.</summary>
    public static BsonValue From(long value) => (BsonInt64)value;

    /// <summary>Converts a nullable <see cref="long"/>.</summary>
    public static BsonValue From(long? value) => value is { } v ? (BsonInt64)v : BsonNull.Value;

    /// <summary>Converts a <see cref="double"/>.</summary>
    public static BsonValue From(double value) => (BsonDouble)value;

    /// <summary>Converts a nullable <see cref="double"/>.</summary>
    public static BsonValue From(double? value) => value is { } v ? (BsonDouble)v : BsonNull.Value;

    /// <summary>Converts a <see cref="decimal"/> to Decimal128.</summary>
    public static BsonValue From(decimal value) => new BsonDecimal128(new Decimal128(value));

    /// <summary>Converts a nullable <see cref="decimal"/>.</summary>
    public static BsonValue From(decimal? value) => value is { } v ? From(v) : BsonNull.Value;

    /// <summary>Converts a <see cref="Decimal128"/>.</summary>
    public static BsonValue From(Decimal128 value) => new BsonDecimal128(value);

    /// <summary>Converts a nullable <see cref="Decimal128"/>.</summary>
    public static BsonValue From(Decimal128? value) => value is { } v ? new BsonDecimal128(v) : BsonNull.Value;

    /// <summary>Converts a <see cref="bool"/>.</summary>
    public static BsonValue From(bool value) => (BsonBoolean)value;

    /// <summary>Converts a nullable <see cref="bool"/>.</summary>
    public static BsonValue From(bool? value) => value is { } v ? (BsonBoolean)v : BsonNull.Value;

    /// <summary>Converts a <see cref="Guid"/> to binary subtype 4.</summary>
    public static BsonValue From(Guid value) => new BsonBinaryData(value, GuidRepresentation.Standard);

    /// <summary>Converts a nullable <see cref="Guid"/>.</summary>
    public static BsonValue From(Guid? value) => value is { } v ? From(v) : BsonNull.Value;

    /// <summary>Converts an <see cref="ObjectId"/>.</summary>
    public static BsonValue From(ObjectId value) => new BsonObjectId(value);

    /// <summary>Converts a nullable <see cref="ObjectId"/>.</summary>
    public static BsonValue From(ObjectId? value) => value is { } v ? new BsonObjectId(v) : BsonNull.Value;

    /// <summary>Converts bytes to binary subtype 0.</summary>
    public static BsonValue From(byte[]? value) => value is null ? BsonNull.Value : new BsonBinaryData(value);

    /// <summary>Converts a <see cref="DateTime"/> to a BSON date; <paramref name="parameter"/> names it in errors.</summary>
    public static BsonValue From(DateTime value, string? queryId, string parameter)
        => new BsonDateTime(MongoBsonWriting.ToUtc(value, queryId, null, parameter));

    /// <summary>Converts a nullable <see cref="DateTime"/>.</summary>
    public static BsonValue From(DateTime? value, string? queryId, string parameter)
        => value is { } v ? From(v, queryId, parameter) : BsonNull.Value;

    /// <summary>Converts a sequence of scalars to a BSON array (null becomes BSON null).</summary>
    public static BsonValue FromList<T>(IEnumerable<T>? values, Func<T, BsonValue> element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (values is null)
        {
            return BsonNull.Value;
        }

        var array = values is IReadOnlyCollection<T> collection ? new BsonArray(collection.Count) : [];
        foreach (var value in values)
        {
            array.Add(element(value));
        }

        return array;
    }

    /// <summary>
    /// Wraps a value as <c>{ "$literal": value }</c>. Generated pipelines use it wherever the server evaluates an
    /// aggregation expression, so a string argument such as <c>"$password"</c> stays a string instead of becoming a
    /// field path.
    /// </summary>
    public static BsonDocument Literal(BsonValue value) => new("$literal", value);
}
