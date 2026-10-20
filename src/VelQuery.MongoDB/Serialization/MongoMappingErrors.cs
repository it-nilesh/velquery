using System;
using MongoDB.Bson;

namespace VelQuery.MongoDB.Serialization;

/// <summary>
/// Mapping failures raised by generated serializers. Messages name the query, the field path and the types involved,
/// never the value (VQ-SEC-003). Field paths are only concatenated here, on the failure path.
/// </summary>
internal static class MongoMappingErrors
{
    public static string Column(string? path, string field) => path is null ? field : path + "." + field;

    public static VelQueryMappingException Null(string? queryId, string? path, string field, Type target)
        => VelQueryMappingException.NullToNonNullable(queryId, Column(path, field), target);

    public static VelQueryMappingException Missing(string? queryId, string? path, string field, Type target)
        => new(queryId, Column(path, field), target, "the required field is missing from the document. Make the member nullable or fix the data or projection.");

    public static VelQueryMappingException Duplicate(string? queryId, string? path, string field, Type target)
        => new(queryId, Column(path, field), target, "the field occurs more than once in the document, so the value to use is ambiguous.");

    public static VelQueryMappingException Unconvertible(string? queryId, string? path, string field, BsonType source, Type target)
        => new(queryId, Column(path, field), target, $"a BSON {source} value cannot be converted to '{target.Name}' without loss.");

    public static VelQueryMappingException OutOfRange(string? queryId, string? path, string field, BsonType source, Type target)
        => new(queryId, Column(path, field), target, $"the BSON {source} value is outside the range of '{target.Name}'.");

    public static VelQueryMappingException Inexact(string? queryId, string? path, string field, BsonType source, Type target)
        => new(queryId, Column(path, field), target, $"the BSON {source} value has more precision than '{target.Name}' can represent, so it cannot be converted without loss.");

    public static VelQueryMappingException BinarySubType(string? queryId, string? path, string field, BsonBinarySubType subType, Type target)
        => new(queryId, Column(path, field), target, $"BSON binary subtype {subType} cannot be read as '{target.Name}'. Guids must use subtype UuidStandard (4); the legacy subtype 3 has an ambiguous byte order.");

    public static VelQueryMappingException NullMember(string? queryId, string? path, string field, Type target)
        => new(queryId, Column(path, field), target, "the member is null but is declared non-nullable, so the written document could not be read back in strict mode.");

    public static VelQueryMappingException UnspecifiedDateTime(string? queryId, string? path, string field)
        => new(queryId, Column(path, field), typeof(DateTime), "the DateTime has DateTimeKind.Unspecified; BSON dates are UTC instants, so use a UTC or local DateTime.");
}
