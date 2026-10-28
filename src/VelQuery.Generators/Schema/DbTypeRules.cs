using System;
using VelQuery.Generators.Sql;

namespace VelQuery.Generators.Schema;

/// <summary>The CLR value a provider returns for a database type, as far as the conversion rules care.</summary>
internal enum ValueClass
{
    Unknown,
    Boolean,
    Integer,
    Integer64,
    Decimal,
    Single,
    Double,
    String,
    Guid,
    DateTime,
    DateTimeUtc,
    DateTimeOffset,
    Date,
    Time,
    Binary,
}

/// <summary>
/// Compile-time mirror of the runtime conversion rules (<c>ValueConverter</c>, ADR-0004): which database types can be
/// read into which member types. Only combinations that fail for every row are reported (VQ1027); unknown database
/// types and SQLite (whose driver converts natively, ADR-0017) are never reported.
/// </summary>
internal static class DbTypeRules
{
    public static ValueClass Classify(SqlDialect dialect, string? dbType)
    {
        if (dbType is null || dialect == SqlDialect.Sqlite || dialect == SqlDialect.Generic)
        {
            return ValueClass.Unknown;
        }

        var type = dbType.Trim().ToLowerInvariant();
        var paren = type.IndexOf('(');
        var size = paren < 0 ? null : type.Substring(paren);
        var name = (paren < 0 ? type : type.Substring(0, paren)).Trim();
        var unsigned = type.IndexOf(" unsigned", StringComparison.Ordinal) >= 0;
        if (name.EndsWith(" unsigned", StringComparison.Ordinal))
        {
            name = name.Substring(0, name.Length - " unsigned".Length);
        }

        return dialect switch
        {
            SqlDialect.SqlServer => name switch
            {
                "bit" => ValueClass.Boolean,
                "tinyint" or "smallint" or "int" => ValueClass.Integer,
                "bigint" => ValueClass.Integer64,
                "decimal" or "numeric" or "money" or "smallmoney" => ValueClass.Decimal,
                "real" => ValueClass.Single,
                "float" => ValueClass.Double,
                "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "xml" or "sysname" => ValueClass.String,
                "uniqueidentifier" => ValueClass.Guid,
                "datetime" or "datetime2" or "smalldatetime" => ValueClass.DateTime,
                "datetimeoffset" => ValueClass.DateTimeOffset,
                "date" => ValueClass.Date,
                "time" => ValueClass.Time,
                "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => ValueClass.Binary,
                _ => ValueClass.Unknown,
            },
            SqlDialect.PostgreSql => name switch
            {
                "boolean" or "bool" => ValueClass.Boolean,
                "smallint" or "int2" or "integer" or "int" or "int4" => ValueClass.Integer,
                "bigint" or "int8" => ValueClass.Integer64,
                "numeric" or "decimal" or "money" => ValueClass.Decimal,
                "real" or "float4" => ValueClass.Single,
                "double precision" or "float8" => ValueClass.Double,
                "text" or "character varying" or "varchar" or "character" or "char" or "bpchar" or "name" or "citext" or "json" or "jsonb" or "xml" => ValueClass.String,
                "uuid" => ValueClass.Guid,
                "timestamp without time zone" or "timestamp" => ValueClass.DateTime,
                "timestamp with time zone" or "timestamptz" => ValueClass.DateTimeUtc,
                "date" => ValueClass.Date,
                "time without time zone" or "time" or "interval" => ValueClass.Time,
                "bytea" => ValueClass.Binary,
                _ => ValueClass.Unknown,
            },
            SqlDialect.MySql => name switch
            {
                // tinyint(1) is bool or sbyte depending on the driver's TreatTinyAsBoolean; char(36)/binary(16) may be Guid.
                "tinyint" when size == "(1)" => ValueClass.Unknown,
                "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "year" => ValueClass.Integer,
                "bigint" => unsigned ? ValueClass.Unknown : ValueClass.Integer64,
                "decimal" or "numeric" => ValueClass.Decimal,
                "float" => ValueClass.Single,
                "double" or "real" or "double precision" => ValueClass.Double,
                "char" when size == "(36)" => ValueClass.Unknown,
                "binary" when size == "(16)" => ValueClass.Unknown,
                "char" or "varchar" or "tinytext" or "text" or "mediumtext" or "longtext" or "enum" or "set" or "json" => ValueClass.String,
                "datetime" or "timestamp" => ValueClass.DateTime,
                "date" => ValueClass.Date,
                "time" => ValueClass.Time,
                "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" => ValueClass.Binary,
                _ => ValueClass.Unknown,
            },
            _ => ValueClass.Unknown,
        };
    }

    /// <summary>False only when no value of <paramref name="value"/> can be converted to <paramref name="target"/>.</summary>
    public static bool CanConvert(ValueClass value, TargetKind target)
    {
        if (value == ValueClass.Unknown || target == TargetKind.Other)
        {
            return true;
        }

        var integer = value is ValueClass.Integer or ValueClass.Integer64;
        return target switch
        {
            TargetKind.String or TargetKind.Char => value == ValueClass.String,
            TargetKind.Boolean => value == ValueClass.Boolean,
            TargetKind.Integral or TargetKind.Enum => integer,
            TargetKind.Single => value == ValueClass.Single,
            TargetKind.Double => value is ValueClass.Single or ValueClass.Double or ValueClass.Integer,
            TargetKind.Decimal => value == ValueClass.Decimal || integer,
            TargetKind.Guid => value == ValueClass.Guid,
            TargetKind.DateTime => value is ValueClass.DateTime or ValueClass.DateTimeUtc or ValueClass.Date,
            TargetKind.DateTimeOffset => value is ValueClass.DateTimeOffset or ValueClass.DateTimeUtc,
            TargetKind.DateOnly => value is ValueClass.Date or ValueClass.DateTime,
            TargetKind.TimeOnly or TargetKind.TimeSpan => value == ValueClass.Time,
            TargetKind.Binary => value == ValueClass.Binary,
            _ => true,
        };
    }

    /// <summary>Classifies a non-nullable member type as displayed by the generator.</summary>
    public static TargetKind Target(string valueType, bool isEnum) => isEnum ? TargetKind.Enum : valueType switch
    {
        "bool" => TargetKind.Boolean,
        "char" => TargetKind.Char,
        "sbyte" or "byte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" => TargetKind.Integral,
        "float" => TargetKind.Single,
        "double" => TargetKind.Double,
        "decimal" => TargetKind.Decimal,
        "string" => TargetKind.String,
        "global::System.Guid" => TargetKind.Guid,
        "global::System.DateTime" => TargetKind.DateTime,
        "global::System.DateTimeOffset" => TargetKind.DateTimeOffset,
        "global::System.DateOnly" => TargetKind.DateOnly,
        "global::System.TimeOnly" => TargetKind.TimeOnly,
        "global::System.TimeSpan" => TargetKind.TimeSpan,
        "byte[]" => TargetKind.Binary,
        _ => TargetKind.Other,
    };
}
