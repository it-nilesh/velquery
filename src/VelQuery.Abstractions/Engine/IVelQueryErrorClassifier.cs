using System;
using VelQuery.Providers;

namespace VelQuery.Engine;

/// <summary>
/// Engine-specific knowledge needed to apply VelQuery's error contract (VQ-FR-026) to any database engine: which
/// exceptions come from the engine's driver, and which of those mean timeout or cancellation.
/// </summary>
public interface IVelQueryErrorClassifier
{
    /// <summary>Classifies a driver exception.</summary>
    ProviderErrorKind Classify(Exception exception);

    /// <summary>
    /// True when <paramref name="exception"/> originates from the engine's driver and should be wrapped in
    /// <see cref="VelQueryProviderException"/>. Other exceptions (for example from caller code) pass through unchanged.
    /// </summary>
    bool IsProviderException(Exception exception);
}
