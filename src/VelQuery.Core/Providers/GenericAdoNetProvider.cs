using System;
using System.Data.Common;
using VelQuery.Parameters;

namespace VelQuery.Providers;

/// <summary>
/// Provider for any ADO.NET driver without a dedicated VelQuery package (PRD amendment VQ-DB-003). It applies only
/// generic behavior, so guarantees are reduced: provider timeouts are reported as <see cref="VelQueryProviderException"/>
/// (not <see cref="VelQueryTimeoutException"/>) unless the driver throws <see cref="TimeoutException"/>, values are not
/// normalized, plan-stable parameter sizing is not applied, and there is no bulk path. Prefer a dedicated provider.
/// </summary>
public sealed class GenericAdoNetProvider : IVelQueryProvider
{
    private readonly GenericAdoNetProviderOptions _options;

    /// <summary>Creates a provider with the given dialect options.</summary>
    public GenericAdoNetProvider(GenericAdoNetProviderOptions? options = null)
    {
        _options = options ?? new GenericAdoNetProviderOptions();
        if (_options.IdentifierQuote.Length != 1 || _options.IdentifierQuoteEnd is { Length: not 1 })
        {
            throw new ArgumentException("Identifier quotes must be single characters.", nameof(options));
        }
    }

    /// <inheritdoc />
    public string DialectId => _options.DialectId;

    /// <inheritdoc />
    public ProviderCapabilities Capabilities => _options.Capabilities;

    /// <inheritdoc />
    public string FormatParameterName(string name) => _options.ParameterPrefix + name;

    /// <summary>Applies nothing beyond the generic metadata already set from <see cref="ParameterSpec"/>.</summary>
    public void ConfigureParameter(DbParameter parameter, in ParameterSpec spec, object? value)
    {
    }

    /// <inheritdoc />
    public string QuoteIdentifier(ReadOnlySpan<char> validatedIdentifier)
    {
        if (validatedIdentifier.IsEmpty || validatedIdentifier.Length > _options.MaxIdentifierLength)
        {
            throw new ArgumentException($"Identifiers must be 1-{_options.MaxIdentifierLength} characters.", nameof(validatedIdentifier));
        }

        var open = _options.IdentifierQuote;
        var close = _options.IdentifierQuoteEnd ?? open;
        return open + validatedIdentifier.ToString().Replace(close, close + close, StringComparison.Ordinal) + close;
    }

    /// <inheritdoc />
    public ProviderErrorKind Classify(Exception exception)
        => exception is TimeoutException || exception.InnerException is TimeoutException ? ProviderErrorKind.Timeout : ProviderErrorKind.Other;
}

/// <summary>Dialect settings for <see cref="GenericAdoNetProvider"/>.</summary>
public sealed record GenericAdoNetProviderOptions
{
    /// <summary>Dialect identifier used in diagnostics. Default <c>generic</c>.</summary>
    public string DialectId { get; init; } = "generic";

    /// <summary>Prefix the driver expects on parameter names (for example <c>@</c>, <c>:</c> or empty). Default <c>@</c>.</summary>
    public string ParameterPrefix { get; init; } = "@";

    /// <summary>Opening identifier quote (ANSI <c>"</c>, MySQL <c>`</c>, SQL Server <c>[</c>). Default <c>"</c>.</summary>
    public string IdentifierQuote { get; init; } = "\"";

    /// <summary>Closing identifier quote when it differs from the opening one (for example <c>]</c>).</summary>
    public string? IdentifierQuoteEnd { get; init; }

    /// <summary>Maximum identifier length. Default 128.</summary>
    public int MaxIdentifierLength { get; init; } = 128;

    /// <summary>
    /// Capabilities the driver supports. Default <see cref="ProviderCapabilities.MultipleResults"/>; declare
    /// <see cref="ProviderCapabilities.NativeConversions"/> only for drivers whose typed getters convert storage types.
    /// </summary>
    public ProviderCapabilities Capabilities { get; init; } = ProviderCapabilities.MultipleResults;
}
