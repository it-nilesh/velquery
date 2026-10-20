using System;
using System.Data.Common;
using System.Threading;
using Microsoft.Data.Sqlite;
using VelQuery.Parameters;
using VelQuery.Providers;

namespace VelQuery.Sqlite;

/// <summary>SQLite dialect and Microsoft.Data.Sqlite behavior.</summary>
/// <remarks>
/// <para>SQLite stores values in five storage classes (NULL, INTEGER, REAL, TEXT, BLOB), so most CLR types are stored
/// as something else: <see cref="DateTime"/>, <see cref="DateTimeOffset"/>, <see cref="DateOnly"/>,
/// <see cref="TimeOnly"/>, <see cref="TimeSpan"/>, <see cref="Guid"/> and <see cref="decimal"/> as TEXT,
/// <see cref="bool"/> as INTEGER. The provider therefore declares <see cref="ProviderCapabilities.NativeConversions"/>:
/// mappers read through Microsoft.Data.Sqlite's typed getters, which parse and convert those representations.</para>
/// <para>Those getters follow SQLite's own coercion rules, which are weaker than VelQuery's lossless
/// <c>ValueConverter</c>: integral targets truncate REAL values and read non-numeric TEXT as 0, <see cref="bool"/>
/// reads non-numeric TEXT as <see langword="false"/>, and a <see cref="DateTime"/> comes back with
/// <see cref="DateTimeKind.Unspecified"/>. Integral narrowing is overflow-checked. Use <c>STRICT</c> tables so SQLite
/// rejects mismatched values when they are written.</para>
/// <para>Microsoft.Data.Sqlite applies <c>CommandTimeout</c> only while waiting for a lock (SQLITE_BUSY/SQLITE_LOCKED);
/// a long-running statement is not interrupted by the timeout, and the cancellation token is observed only before
/// execution and between rows.</para>
/// </remarks>
public sealed class SqliteProvider : IVelQueryProvider
{
    /// <summary>
    /// Identifier length accepted by <see cref="QuoteIdentifier"/>. SQLite itself has no limit; this bounds the
    /// allowlisted names VelQuery builds into SQL.
    /// </summary>
    public const int MaxIdentifierLength = 128;

    // Primary result codes (https://sqlite.org/rescode.html). Extended codes carry the primary code in the low byte.
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteInterrupt = 9;

    /// <summary>Shared instance.</summary>
    public static SqliteProvider Instance { get; } = new();

    /// <inheritdoc />
    public string DialectId => "sqlite";

    /// <summary>
    /// Multiple statements per command (one result set per statement), explicit preparation, and native conversions.
    /// SQLite has no stored procedures or output parameters.
    /// </summary>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.MultipleResults | ProviderCapabilities.NativeConversions;

    /// <summary>
    /// Returns <c>@name</c>. Microsoft.Data.Sqlite would also bind a bare name to <c>$name</c> or <c>:name</c>, but the
    /// explicit prefix keeps binding aligned with the <c>@name</c> placeholders the generator checks (VQ-FR-033).
    /// </summary>
    public string FormatParameterName(string name) => "@" + name;

    /// <summary>
    /// Applies <see cref="ParameterSpec.ProviderDbType"/> as a <see cref="SqliteType"/> when supplied (for example
    /// <see cref="SqliteType.Blob"/> to store a <see cref="Guid"/> as 16 bytes). Otherwise the driver chooses the storage
    /// class from the value, which is the same representation its typed getters read back. Values are never rewritten.
    /// </summary>
    public void ConfigureParameter(DbParameter parameter, in ParameterSpec spec, object? value)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (parameter is SqliteParameter sqlite && spec.ProviderDbType is { } native)
        {
            sqlite.SqliteType = (SqliteType)native;
        }
    }

    /// <inheritdoc />
    public string QuoteIdentifier(ReadOnlySpan<char> validatedIdentifier)
    {
        if (validatedIdentifier.IsEmpty || validatedIdentifier.Length > MaxIdentifierLength || validatedIdentifier.Contains('\0'))
        {
            throw new ArgumentException($"SQLite identifiers must be 1-{MaxIdentifierLength} characters without NUL.", nameof(validatedIdentifier));
        }

        return "\"" + validatedIdentifier.ToString().Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// Microsoft.Data.Sqlite's <c>Cancel()</c> is a no-op and its async methods only observe the token between steps, so a
    /// single long step (an aggregate, a large sort) would run to completion. The token instead calls
    /// <c>sqlite3_interrupt</c> on the command's connection, which aborts the running statement with SQLITE_INTERRUPT; the
    /// caller then receives <see cref="OperationCanceledException"/>. VelQuery disposes the registration before the
    /// connection is released, so the interrupt can never reach another operation.
    /// </summary>
    public CancellationTokenRegistration RegisterCancellation(DbCommand command, CancellationToken cancellationToken)
        => command?.Connection is SqliteConnection { Handle: { } handle }
            ? cancellationToken.UnsafeRegister(static state => SQLitePCL.raw.sqlite3_interrupt((SQLitePCL.sqlite3)state!), handle)
            : default;

    /// <summary>
    /// SQLITE_BUSY and SQLITE_LOCKED (including extended codes) are timeouts: Microsoft.Data.Sqlite retries them until
    /// the command timeout elapses and only then throws. SQLITE_INTERRUPT is a cancellation by the provider (an
    /// interrupt not caused by the caller's token, which VelQuery checks first). Everything else, including constraint
    /// violations, is a provider error that keeps the original <see cref="SqliteException"/> as its inner exception.
    /// </summary>
    public ProviderErrorKind Classify(Exception exception) => exception switch
    {
        SqliteException { SqliteErrorCode: SqliteBusy or SqliteLocked } => ProviderErrorKind.Timeout,
        SqliteException { SqliteErrorCode: SqliteInterrupt } => ProviderErrorKind.Cancelled,
        _ => ProviderErrorKind.Other,
    };
}
