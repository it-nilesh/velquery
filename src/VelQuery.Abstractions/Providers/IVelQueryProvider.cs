using System;
using System.Data.Common;
using VelQuery.Parameters;

namespace VelQuery.Providers;

/// <summary>Dialect- and driver-specific behavior used by the VelQuery runtime and generated code.</summary>
public interface IVelQueryProvider
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

    /// <summary>Classifies a driver exception.</summary>
    ProviderErrorKind Classify(Exception exception);
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

    /// <summary>Multiple result sets per command.</summary>
    MultipleResults = 1 << 0,

    /// <summary>Stored procedures with output parameters.</summary>
    StoredProcedures = 1 << 1,

    /// <summary>Output and input/output parameters.</summary>
    OutputParameters = 1 << 2,

    /// <summary>Explicit command preparation.</summary>
    Prepare = 1 << 3,
}
