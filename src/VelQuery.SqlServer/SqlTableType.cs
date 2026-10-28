using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using Microsoft.Data.SqlClient.Server;
using VelQuery.Parameters;

namespace VelQuery.SqlServer;

/// <summary>
/// A SQL Server user-defined table type (<c>CREATE TYPE dbo.IdList AS TABLE (...)</c>) and how to fill it from
/// <typeparamref name="T"/>, for table-valued parameters. Create one per table type and reuse it; it is immutable.
/// </summary>
/// <example>
/// <code>
/// static readonly SqlTableType&lt;int&gt; IdList = SqlTableType&lt;int&gt;.Create("dbo.IdList").Column("Id", id => id);
///
/// var rows = await session.QueryAsync&lt;Product&gt;(
///     "SELECT p.Id, p.Name FROM dbo.Products p JOIN @ids i ON i.Id = p.Id",
///     new QueryParameters().Add("ids", IdList.Rows(ids)), ct);
/// </code>
/// </example>
/// <remarks>
/// Rows are streamed to the server as they are enumerated (no <c>DataTable</c>, no reflection), and columns are sent in
/// the order they are declared, which must match the table type's column order. An empty sequence is sent as an empty
/// table. The sequence is enumerated once, when the command runs (its first row is read by <see cref="Rows"/>). Values are
/// converted only losslessly: a decimal column needs explicit precision and scale, and values that would be rounded are
/// rejected.
/// </remarks>
public sealed class SqlTableType<T>
{
    private readonly ImmutableArray<(SqlMetaData Meta, Func<T, object?> Getter)> _columns;

    private SqlTableType(string typeName, ImmutableArray<(SqlMetaData Meta, Func<T, object?> Getter)> columns)
    {
        TypeName = typeName;
        _columns = columns;
    }

    /// <summary>The table type's name, for example <c>dbo.IdList</c>.</summary>
    public string TypeName { get; }

    /// <summary>Starts a table type definition.</summary>
    /// <param name="typeName">The user-defined table type, optionally schema-qualified (<c>dbo.IdList</c>).</param>
    public static SqlTableType<T> Create(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return new SqlTableType<T>(typeName, []);
    }

    /// <summary>Adds the next column. The SQL type is inferred from <typeparamref name="TValue"/> unless given.</summary>
    /// <param name="name">Column name in the table type.</param>
    /// <param name="getter">Reads the column value from a row.</param>
    /// <param name="sqlType">Overrides the inferred SQL type (for example <see cref="SqlDbType.VarChar"/>).</param>
    /// <param name="maxLength">Length for string and binary columns; defaults to MAX (-1).</param>
    /// <param name="precision">Precision; required for decimal columns.</param>
    /// <param name="scale">Scale; required for decimal columns.</param>
    public SqlTableType<T> Column<TValue>(string name, Func<T, TValue> getter, SqlDbType? sqlType = null, long? maxLength = null, byte? precision = null, byte? scale = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(getter);
        var type = sqlType ?? Infer(typeof(TValue))
            ?? throw new ArgumentException($"Column '{name}': no SQL type can be inferred for {typeof(TValue).Name}; pass sqlType.", nameof(sqlType));
        SqlMetaData meta = type switch
        {
            SqlDbType.NVarChar or SqlDbType.VarChar or SqlDbType.NChar or SqlDbType.Char or SqlDbType.VarBinary or SqlDbType.Binary
                => new SqlMetaData(name, type, maxLength ?? SqlMetaData.Max),
            SqlDbType.Decimal when precision is { } p && scale is { } s => new SqlMetaData(name, type, p, s),
            SqlDbType.Decimal => throw new ArgumentException($"Column '{name}': decimal columns need precision and scale.", nameof(precision)),
            _ => new SqlMetaData(name, type),
        };

        // DateOnly/TimeOnly are not SqlDataRecord types: convert to the DateTime/TimeSpan SQL Server stores. Enums are
        // sent as their integral value. Decimals that would be rounded to the column's scale are rejected (lossless only).
        var decimalScale = type == SqlDbType.Decimal ? scale : null;
        Func<T, object?> read = row => getter(row) switch
        {
            null => null,
            DateOnly d => d.ToDateTime(TimeOnly.MinValue),
            TimeOnly t => t.ToTimeSpan(),
            Enum e => EnumValue(e),
            decimal m when decimalScale is { } s && decimal.Round(m, s) != m
                => throw new ArgumentException($"Column '{name}' of table type '{TypeName}': a value has more than {s} decimal places and would be rounded."),
            var value => value,
        };
        return new SqlTableType<T>(TypeName, _columns.Add((meta, read)));
    }

    /// <summary>A table-valued parameter value holding <paramref name="rows"/>; pass it as a parameter value.</summary>
    public SqlTableValue Rows(IEnumerable<T> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (_columns.IsEmpty)
        {
            throw new InvalidOperationException($"Table type '{TypeName}' has no columns; call Column(...) first.");
        }

        return new SqlTableValue(TypeName, Records(rows));
    }

    private IEnumerable<SqlDataRecord>? Records(IEnumerable<T> rows)
    {
        // SqlClient rejects an empty record sequence (it reads the first record for metadata); null sends an empty
        // table. The first row is read here, and the same enumerator continues in Stream: the source is enumerated once.
        var enumerator = rows.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            enumerator.Dispose();
            return null;
        }

        return Stream(enumerator);
    }

    private IEnumerable<SqlDataRecord> Stream(IEnumerator<T> rows)
    {
        var meta = new SqlMetaData[_columns.Length];
        for (var i = 0; i < meta.Length; i++)
        {
            meta[i] = _columns[i].Meta;
        }

        // One record reused for every row, as SqlClient recommends for streamed table-valued parameters.
        var record = new SqlDataRecord(meta);
        try
        {
            do
            {
                var row = rows.Current;
                for (var i = 0; i < _columns.Length; i++)
                {
                    record.SetValue(i, _columns[i].Getter(row) ?? DBNull.Value);
                }

                yield return record;
            }
            while (rows.MoveNext());
        }
        finally
        {
            rows.Dispose();
        }
    }

    // Each arm is boxed as its own type: without the casts the switch's common type would be long, and a tinyint or
    // int column would receive a long.
    private static object EnumValue(Enum value) => value.GetTypeCode() switch
    {
        TypeCode.Byte => (object)Convert.ToByte(value, System.Globalization.CultureInfo.InvariantCulture),
        TypeCode.Int16 => (object)Convert.ToInt16(value, System.Globalization.CultureInfo.InvariantCulture),
        TypeCode.Int64 => (object)Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
        _ => (object)Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static SqlDbType? Infer(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum)
        {
            type = Enum.GetUnderlyingType(type);
        }

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => SqlDbType.Bit,
            TypeCode.Byte => SqlDbType.TinyInt,
            TypeCode.Int16 => SqlDbType.SmallInt,
            TypeCode.Int32 => SqlDbType.Int,
            TypeCode.Int64 => SqlDbType.BigInt,
            TypeCode.Single => SqlDbType.Real,
            TypeCode.Double => SqlDbType.Float,
            TypeCode.Decimal => SqlDbType.Decimal,
            TypeCode.String => SqlDbType.NVarChar,
            TypeCode.DateTime => SqlDbType.DateTime2,
            _ when type == typeof(Guid) => SqlDbType.UniqueIdentifier,
            _ when type == typeof(DateTimeOffset) => SqlDbType.DateTimeOffset,
            _ when type == typeof(DateOnly) => SqlDbType.Date,
            _ when type == typeof(TimeOnly) || type == typeof(TimeSpan) => SqlDbType.Time,
            _ when type == typeof(byte[]) => SqlDbType.VarBinary,
            _ => null,
        };
    }
}

/// <summary>
/// A table-valued parameter value created by <see cref="SqlTableType{T}.Rows"/>. Bind it like any other value, in
/// <see cref="QueryParameters"/> or as a generated contract's method parameter.
/// </summary>
public sealed class SqlTableValue : IProviderParameterValue
{
    internal SqlTableValue(string typeName, IEnumerable<SqlDataRecord>? records)
    {
        TypeName = typeName;
        Records = records;
    }

    /// <summary>The table type's name.</summary>
    public string TypeName { get; }

    /// <summary>The rows, or null for an empty table.</summary>
    internal IEnumerable<SqlDataRecord>? Records { get; }
}
