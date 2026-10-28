using System;
using System.Data;
using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using VelQuery.Parameters;
using VelQuery.Providers;

namespace VelQuery.PostgreSQL;

/// <summary>PostgreSQL dialect and Npgsql behavior.</summary>
public sealed class PostgreSqlProvider : IVelQueryProvider
{
    /// <summary>PostgreSQL's identifier length limit (NAMEDATALEN - 1).</summary>
    public const int MaxIdentifierLength = 63;

    private const string QueryCanceledSqlState = "57014";

    /// <summary>Shared instance.</summary>
    public static PostgreSqlProvider Instance { get; } = new();

    /// <inheritdoc />
    public string DialectId => "postgresql";

    /// <inheritdoc />
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.MultipleResults | ProviderCapabilities.StoredProcedures | ProviderCapabilities.OutputParameters | ProviderCapabilities.Batches;

    /// <summary>
    /// Npgsql binds <c>@name</c> placeholders to parameters named <c>name</c>, so the logical name is used as-is.
    /// </summary>
    public string FormatParameterName(string name) => name;

    /// <summary>
    /// Applies the native <see cref="NpgsqlDbType"/> when supplied. Unlike SQL Server, PostgreSQL caches plans by
    /// type rather than declared length, so no string size bucketing is needed. A UTC <see cref="DateTime"/> bound with
    /// the inferred <see cref="DbType.DateTime2"/> is sent as <c>timestamptz</c>, matching Npgsql's own
    /// Kind-based mapping instead of failing, and a <see cref="DateTimeOffset"/> with a non-zero offset is sent as the
    /// same instant in UTC.
    /// </summary>
    public void ConfigureParameter(DbParameter parameter, in ParameterSpec spec, object? value)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (parameter is not NpgsqlParameter npgsql)
        {
            return;
        }

        if (spec.ProviderDbType is { } native)
        {
            npgsql.NpgsqlDbType = (NpgsqlDbType)native;
        }
        else if (spec.DbType == DbType.DateTime2 && value is DateTime { Kind: DateTimeKind.Utc })
        {
            npgsql.NpgsqlDbType = NpgsqlDbType.TimestampTz;
        }

        // Npgsql only writes offset-zero DateTimeOffset values to timestamptz. timestamptz stores an instant, so the
        // same instant in UTC is equivalent.
        if (value is DateTimeOffset { Offset.Ticks: not 0 } offset)
        {
            npgsql.Value = offset.ToUniversalTime();
        }
    }

    /// <inheritdoc />
    public string QuoteIdentifier(ReadOnlySpan<char> validatedIdentifier)
    {
        if (validatedIdentifier.IsEmpty || validatedIdentifier.Length > MaxIdentifierLength)
        {
            throw new ArgumentException($"PostgreSQL identifiers must be 1-{MaxIdentifierLength} characters.", nameof(validatedIdentifier));
        }

        return "\"" + validatedIdentifier.ToString().Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// Npgsql reports a command timeout as an <see cref="NpgsqlException"/> wrapping <see cref="TimeoutException"/>.
    /// A server-side <c>query_canceled</c> (57014) not caused by the caller's token is a cancellation by the provider
    /// (for example <c>statement_timeout</c>), which VelQuery reports as a timeout.
    /// </summary>
    public ProviderErrorKind Classify(Exception exception) => exception switch
    {
        NpgsqlException { InnerException: TimeoutException } => ProviderErrorKind.Timeout,
        PostgresException { SqlState: QueryCanceledSqlState } => ProviderErrorKind.Timeout,
        _ => ProviderErrorKind.Other,
    };
}
