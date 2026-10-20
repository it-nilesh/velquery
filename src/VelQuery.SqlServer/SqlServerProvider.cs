using System;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using VelQuery.Parameters;
using VelQuery.Providers;

namespace VelQuery.SqlServer;

/// <summary>SQL Server dialect and Microsoft.Data.SqlClient behavior.</summary>
public sealed class SqlServerProvider : IVelQueryProvider
{
    /// <summary>Largest non-MAX <c>nvarchar</c> length.</summary>
    public const int MaxUnicodeLength = 4000;

    /// <summary>Largest non-MAX <c>varchar</c>/<c>varbinary</c> length.</summary>
    public const int MaxNonUnicodeLength = 8000;

    private const int TimeoutExpiredNumber = -2;

    /// <summary>Shared instance.</summary>
    public static SqlServerProvider Instance { get; } = new();

    /// <inheritdoc />
    public string DialectId => "sqlserver";

    /// <inheritdoc />
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.MultipleResults | ProviderCapabilities.StoredProcedures | ProviderCapabilities.OutputParameters;

    /// <inheritdoc />
    public string FormatParameterName(string name) => "@" + name;

    /// <summary>
    /// Applies the native <see cref="SqlDbType"/> when supplied. When no size is declared for strings or binary
    /// values, uses a fixed size bucket (4000/8000 or MAX) rather than the value's length so that repeated executions
    /// share one cached plan (PRD §9.4).
    /// </summary>
    public void ConfigureParameter(DbParameter parameter, in ParameterSpec spec, object? value)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (parameter is not SqlParameter sql)
        {
            return;
        }

        if (spec.ProviderDbType is { } native)
        {
            sql.SqlDbType = (SqlDbType)native;
        }

        if (spec.Size is not null)
        {
            return;
        }

        switch (value)
        {
            case string s when spec.DbType is null or DbType.String or DbType.StringFixedLength:
                sql.Size = s.Length <= MaxUnicodeLength ? MaxUnicodeLength : -1;
                break;
            case string s when spec.DbType is DbType.AnsiString or DbType.AnsiStringFixedLength:
                sql.Size = s.Length <= MaxNonUnicodeLength ? MaxNonUnicodeLength : -1;
                break;
            case byte[] b:
                sql.Size = b.Length <= MaxNonUnicodeLength ? MaxNonUnicodeLength : -1;
                break;
            case null when spec.DbType is DbType.String or DbType.AnsiString or DbType.Binary:
                sql.Size = spec.DbType == DbType.String ? MaxUnicodeLength : MaxNonUnicodeLength;
                break;
        }
    }

    /// <inheritdoc />
    public string QuoteIdentifier(ReadOnlySpan<char> validatedIdentifier)
    {
        if (validatedIdentifier.IsEmpty || validatedIdentifier.Length > 128)
        {
            throw new ArgumentException("SQL Server identifiers must be 1-128 characters.", nameof(validatedIdentifier));
        }

        return "[" + validatedIdentifier.ToString().Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    /// <inheritdoc />
    public ProviderErrorKind Classify(Exception exception)
        => exception is SqlException { Number: TimeoutExpiredNumber } ? ProviderErrorKind.Timeout : ProviderErrorKind.Other;
}
