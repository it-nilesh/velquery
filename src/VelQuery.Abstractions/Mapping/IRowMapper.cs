using System.Data.Common;

namespace VelQuery.Mapping;

/// <summary>
/// Maps the current row of a reader to <typeparamref name="T"/>. Instances are bound to one reader shape (resolved
/// ordinals) and are used by a single invocation only.
/// </summary>
public interface IRowMapper<out T>
{
    /// <summary>Maps the reader's current row.</summary>
    T Map(DbDataReader reader);
}

/// <summary>
/// Creates row mappers for a concrete reader shape. Implementations must be immutable and thread-safe; generated
/// factories are emitted at compile time by VelQuery.Generators.
/// </summary>
public interface IRowMapperFactory<out T>
{
    /// <summary>
    /// Resolves column ordinals against <paramref name="reader"/> (before the first row is read) and returns a mapper
    /// for that shape. Throws <see cref="VelQueryMappingException"/> if required columns are missing or ambiguous.
    /// </summary>
    IRowMapper<T> Create(DbDataReader reader, in MappingContext context);

    /// <summary>
    /// True when every mapper this factory creates reads each column at most once and in increasing ordinal order.
    /// VelQuery then opens readers with <see cref="System.Data.CommandBehavior.SequentialAccess"/>, which lets drivers
    /// avoid buffering whole rows. Implementations that cannot guarantee the order must return false (the default).
    /// </summary>
    bool SupportsSequentialAccess => false;
}

/// <summary>Per-invocation information available to row mappers.</summary>
/// <param name="QueryId">Stable diagnostic query identity.</param>
/// <param name="StrictNulls">When true, NULL into a non-nullable target throws instead of producing a default.</param>
/// <param name="NativeConversions">
/// When true, values whose provider type differs from the target are read with <c>GetFieldValue&lt;T&gt;</c> (driver
/// conversion) instead of <c>ValueConverter</c>; see <see cref="Providers.ProviderCapabilities.NativeConversions"/>.
/// </param>
public readonly record struct MappingContext(string? QueryId, bool StrictNulls, bool NativeConversions = false);
