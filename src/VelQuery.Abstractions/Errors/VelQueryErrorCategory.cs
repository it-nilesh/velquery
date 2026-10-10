namespace VelQuery;

/// <summary>Stable classification of VelQuery failures (VQ-FR-026).</summary>
public enum VelQueryErrorCategory
{
    /// <summary>Invalid input, configuration or API usage detected before or during execution.</summary>
    Validation,

    /// <summary>A database value or result shape could not be mapped to the requested type.</summary>
    Mapping,

    /// <summary>A cardinality operation required at least one row and none were returned.</summary>
    NoRows,

    /// <summary>A single-row operation returned more than one row.</summary>
    TooManyRows,

    /// <summary>The provider reported a command timeout.</summary>
    Timeout,

    /// <summary>The caller's <see cref="System.Threading.CancellationToken"/> cancelled the operation.</summary>
    Cancelled,

    /// <summary>The database provider reported a failure; the original exception is preserved as the inner exception.</summary>
    Provider,

    /// <summary>A transaction could not be started, completed or used.</summary>
    Transaction,

    /// <summary>The requested behavior is not supported by the provider or the current execution mode.</summary>
    UnsupportedCapability,
}
