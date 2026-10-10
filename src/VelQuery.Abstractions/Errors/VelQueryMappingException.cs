using System;

namespace VelQuery;

/// <summary>
/// Raised when a result column cannot be mapped to its target member (VQ-FR-017). Identifies the query, column and
/// types involved but never the offending value.
/// </summary>
public sealed class VelQueryMappingException : VelQueryException
{
    /// <summary>Creates a mapping exception.</summary>
    public VelQueryMappingException(string? queryId, string? column, Type? targetType, string reason, Exception? innerException = null)
        : base(VelQueryErrorCategory.Mapping, queryId, Format(queryId, column, targetType, reason), innerException)
    {
        Column = column;
        TargetType = targetType;
    }

    /// <summary>The result column involved, if known.</summary>
    public string? Column { get; }

    /// <summary>The CLR type VelQuery attempted to produce, if known.</summary>
    public Type? TargetType { get; }

    /// <summary>Creates the exception raised when a database NULL meets a non-nullable target (VQ-FR-016).</summary>
    public static VelQueryMappingException NullToNonNullable(string? queryId, string? column, Type targetType)
        => new(queryId, column, targetType, "the column contains NULL but the target is not nullable. Make the target nullable or fix the query.");

    /// <summary>Creates the exception raised when a required column is absent from the result.</summary>
    public static VelQueryMappingException MissingColumn(string? queryId, string column, Type targetType)
        => new(queryId, column, targetType, "the required column is not present in the result set.");

    /// <summary>Creates the exception raised when a value's database type cannot be converted to the target type.</summary>
    public static VelQueryMappingException Unconvertible(string? queryId, string? column, Type sourceType, Type targetType, Exception? inner = null)
        => new(queryId, column, targetType, $"a value of type '{sourceType.Name}' cannot be converted to '{targetType.Name}' without loss.", inner);

    private static string Format(string? queryId, string? column, Type? targetType, string reason)
    {
        var where = column is null ? "result" : $"column '{column}'";
        var target = targetType is null ? string.Empty : $" (target type '{targetType.Name}')";
        return Describe(queryId, $"cannot map {where}{target}: {reason}");
    }
}
