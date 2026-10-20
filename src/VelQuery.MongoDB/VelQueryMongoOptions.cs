using System;

namespace VelQuery.MongoDB;

/// <summary>Options for a <see cref="VelQueryMongoDataSource"/>. Values are copied when the data source is created.</summary>
public sealed class VelQueryMongoOptions
{
    /// <summary>
    /// Default per-operation time limit, used when a contract does not set <c>TimeoutMilliseconds</c>.
    /// <see langword="null"/> (the default) sets no VelQuery limit; the driver's own socket and server-selection
    /// timeouts still apply. See <see cref="VelQueryMongoSession"/> for how the limit is enforced per operation kind.
    /// </summary>
    public TimeSpan? DefaultTimeout { get; set; }

    /// <summary>
    /// Extra time the client waits beyond an operation's server-side <c>maxTimeMS</c> (reads and writes) before
    /// cancelling locally, so the server normally stops the operation first and reports a definite timeout. Defaults to
    /// one second.
    /// </summary>
    public TimeSpan ClientDeadlineGrace { get; set; } = TimeSpan.FromSeconds(1);

    internal VelQueryMongoOptions Snapshot()
    {
        if (DefaultTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new VelQueryValidationException(null, "DefaultTimeout must be positive when set.");
        }

        if (ClientDeadlineGrace < TimeSpan.Zero)
        {
            throw new VelQueryValidationException(null, "ClientDeadlineGrace must not be negative.");
        }

        return new VelQueryMongoOptions { DefaultTimeout = DefaultTimeout, ClientDeadlineGrace = ClientDeadlineGrace };
    }
}
