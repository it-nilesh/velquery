using System;
using System.Data.Common;
using MySqlConnector;
using VelQuery.Parameters;
using VelQuery.Providers;

namespace VelQuery.MySql;

/// <summary>MySQL / MariaDB dialect and MySqlConnector behavior.</summary>
/// <remarks>
/// Values are read exactly as MySqlConnector types them; no driver-side conversions are enabled
/// (<see cref="ProviderCapabilities.NativeConversions"/> is not declared). Notable consequences: <c>TINYINT(1)</c>/<c>BOOL</c>
/// reads as <see cref="bool"/> but <c>BIT(n)</c> reads as <see cref="ulong"/>; <c>DATETIME</c>/<c>TIMESTAMP</c> read as
/// <see cref="DateTime"/> with the connection's <c>DateTimeKind</c> (default Unspecified, so mapping to
/// <see cref="DateTimeOffset"/> requires <c>DateTimeKind=Utc</c>); <c>CHAR(36)</c> reads as <see cref="Guid"/> under the default
/// <c>GuidFormat</c>, and <c>BINARY(16)</c> only with an explicit binary <c>GuidFormat</c> (VelQuery never guesses byte order).
/// </remarks>
public sealed class MySqlProvider : IVelQueryProvider
{
    /// <summary>MySQL's identifier length limit for tables and columns.</summary>
    public const int MaxIdentifierLength = 64;

    // ER_QUERY_TIMEOUT: MySQL max_execution_time / MAX_EXECUTION_TIME() hint exceeded.
    private const int MySqlStatementTimeoutNumber = 3024;

    // ER_STATEMENT_TIMEOUT: MariaDB max_statement_time exceeded.
    private const int MariaDbStatementTimeoutNumber = 1969;

    /// <summary>Shared instance.</summary>
    public static MySqlProvider Instance { get; } = new();

    /// <inheritdoc />
    public string DialectId => "mysql";

    /// <inheritdoc />
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.MultipleResults | ProviderCapabilities.StoredProcedures | ProviderCapabilities.OutputParameters | ProviderCapabilities.Prepare;

    /// <summary>
    /// MySqlConnector binds <c>@name</c> placeholders to parameters named <c>name</c> or <c>@name</c>; the prefixed form is
    /// used so names are unambiguous in driver diagnostics.
    /// </summary>
    public string FormatParameterName(string name) => "@" + name;

    /// <summary>
    /// Applies the native <see cref="MySqlDbType"/> when supplied. No string/binary size bucketing is applied: MySQL has no
    /// shared plan cache keyed by parameter metadata (unprepared commands are sent as text by MySqlConnector, and server-side
    /// prepared statements are per connection and keyed by SQL text only), so declared sizes cannot fragment anything.
    /// </summary>
    public void ConfigureParameter(DbParameter parameter, in ParameterSpec spec, object? value)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (parameter is MySqlParameter mySql && spec.ProviderDbType is { } native)
        {
            mySql.MySqlDbType = (MySqlDbType)native;
        }
    }

    /// <summary>Quotes with backticks, doubling embedded backticks.</summary>
    public string QuoteIdentifier(ReadOnlySpan<char> validatedIdentifier)
    {
        if (validatedIdentifier.IsEmpty || validatedIdentifier.Length > MaxIdentifierLength)
        {
            throw new ArgumentException($"MySQL identifiers must be 1-{MaxIdentifierLength} characters.", nameof(validatedIdentifier));
        }

        return "`" + validatedIdentifier.ToString().Replace("`", "``", StringComparison.Ordinal) + "`";
    }

    /// <summary>
    /// Timeouts are MySqlConnector's <see cref="MySqlErrorCode.CommandTimeoutExpired"/> (it kills the query when
    /// <c>CommandTimeout</c> elapses) and the server's statement time limits (MySQL <c>max_execution_time</c>, error 3024;
    /// MariaDB <c>max_statement_time</c>, error 1969), the equivalent of PostgreSQL's <c>statement_timeout</c>.
    /// Lock wait timeouts (1205) and deadlocks (1213) are concurrency conflicts, not command timeouts, and stay
    /// <see cref="ProviderErrorKind.Other"/> (as SQL Server 1222 and PostgreSQL 55P03 do) with the error number preserved for
    /// the caller's own retry policy. A query killed by another session (1317, <c>KILL QUERY</c>) is neither the caller's
    /// cancellation nor a timeout, so it is also <see cref="ProviderErrorKind.Other"/>.
    /// </summary>
    public ProviderErrorKind Classify(Exception exception) => exception switch
    {
        MySqlException { ErrorCode: MySqlErrorCode.CommandTimeoutExpired } => ProviderErrorKind.Timeout,
        MySqlException { Number: MySqlStatementTimeoutNumber or MariaDbStatementTimeoutNumber } => ProviderErrorKind.Timeout,
        _ => ProviderErrorKind.Other,
    };
}
