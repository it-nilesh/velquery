using System;

namespace VelQuery;

/// <summary>
/// Configuration for a <see cref="VelQueryDataSource"/>. Values are copied when the data source is created; later
/// changes to this instance do not affect existing data sources (no hidden global mutable switch, VQ-FR-010).
/// </summary>
public sealed class VelQueryOptions
{
    /// <summary>
    /// Default command timeout. <see langword="null"/> keeps the provider default; <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// disables the timeout. Zero or negative values are rejected.
    /// </summary>
    public TimeSpan? DefaultTimeout { get; set; }

    /// <summary>When true (default), NULL mapped to a non-nullable target throws <see cref="VelQueryMappingException"/>.</summary>
    public bool StrictMapping { get; set; } = true;

    /// <summary>
    /// Allows reflection-based mapping for result types that have no generated or registered mapper. Off by default;
    /// recommended off for Native AOT and reproducibility (VQ-FR-020).
    /// </summary>
    public bool AllowDynamicFallback { get; set; }

    internal ResolvedOptions Resolve()
    {
        if (DefaultTimeout is { } timeout)
        {
            CommandTimeouts.Validate(timeout, queryId: null);
        }

        return new ResolvedOptions(DefaultTimeout, StrictMapping, AllowDynamicFallback);
    }
}

internal readonly record struct ResolvedOptions(TimeSpan? DefaultTimeout, bool StrictMapping, bool AllowDynamicFallback);
