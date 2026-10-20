using System;
using System.Data.Common;
using System.Threading;
using VelQuery.Engine;
using VelQuery.Parameters;

namespace VelQuery.Providers;

/// <summary>Dialect- and driver-specific behavior used by the ADO.NET engine and generated code.</summary>
public interface IVelQueryProvider : IVelQueryErrorClassifier
{
    /// <summary>Stable dialect identifier, for example <c>sqlserver</c> or <c>postgresql</c>.</summary>
    string DialectId { get; }

    /// <summary>Capabilities supported by this provider.</summary>
    ProviderCapabilities Capabilities { get; }

    /// <summary>Returns the driver parameter name for a prefix-less logical name.</summary>
    string FormatParameterName(string name);

    /// <summary>
    /// Applies provider-specific metadata after generic metadata and the value have been set, for example native types
    /// or stable string sizes that avoid plan-cache fragmentation. May replace the parameter value only with an
    /// equivalent one (for example the same instant in UTC). Must never log <paramref name="value"/>.
    /// </summary>
    void ConfigureParameter(DbParameter parameter, in ParameterSpec spec, object? value);

    /// <summary>Quotes an identifier that has already been validated against an allowlist (VQ-FR-042).</summary>
    string QuoteIdentifier(ReadOnlySpan<char> validatedIdentifier);

    /// <summary>
    /// Hooks the caller's token to interrupt a running command, for drivers whose async APIs do not cancel execution
    /// themselves (SQLite). VelQuery disposes the registration before the command's connection is released, so a late
    /// cancellation can never reach another operation on a pooled connection. Only called when the token can be
    /// cancelled; the default does nothing.
    /// </summary>
    CancellationTokenRegistration RegisterCancellation(DbCommand command, CancellationToken cancellationToken) => default;

    /// <summary>ADO.NET drivers report failures as <see cref="DbException"/>.</summary>
    bool IVelQueryErrorClassifier.IsProviderException(Exception exception) => exception is DbException;
}

/// <summary>Provider error classification.</summary>
public enum ProviderErrorKind
{
    /// <summary>Not a recognized special case.</summary>
    Other,

    /// <summary>The command or connection timed out.</summary>
    Timeout,

    /// <summary>The driver reports that the operation was cancelled.</summary>
    Cancelled,
}

/// <summary>Features a provider supports.</summary>
[Flags]
public enum ProviderCapabilities
{
    /// <summary>No optional capabilities.</summary>
    None = 0,

    /// <summary>Multiple result sets per command. Required by <c>QueryMultipleAsync</c>.</summary>
    MultipleResults = 1 << 0,

    /// <summary><see cref="System.Data.CommandType.StoredProcedure"/> commands. Required by <c>AsStoredProcedure()</c> and stored-procedure descriptors.</summary>
    StoredProcedures = 1 << 1,

    /// <summary>Output, input/output and return-value parameters.</summary>
    OutputParameters = 1 << 2,

    /// <summary>
    /// The driver's typed getters (<c>GetFieldValue&lt;T&gt;</c>) perform the documented conversions for its storage
    /// model (for example SQLite storing dates as TEXT and decimals as REAL). Mappers then ask the driver for the target
    /// type instead of applying <c>ValueConverter</c> to <c>GetValue()</c>. Strict NULL handling is unchanged.
    /// </summary>
    NativeConversions = 1 << 4,
}
