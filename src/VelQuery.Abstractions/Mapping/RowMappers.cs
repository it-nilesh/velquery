using System;
using System.Threading;

namespace VelQuery.Mapping;

/// <summary>
/// Process-wide registry of compile-time row mapper factories, keyed statically by result type. Lookups are a static
/// field read (no dictionary, no reflection), which keeps the path trimming and Native AOT safe.
/// </summary>
/// <remarks>Generated code registers factories from a module initializer. Built-in scalar types are always available.</remarks>
public static class RowMappers<T>
{
    private static IRowMapperFactory<T>? s_factory = ScalarRowMapperFactory<T>.IsSupported ? ScalarRowMapperFactory<T>.Instance : null;

    /// <summary>The registered factory for <typeparamref name="T"/>, or <see langword="null"/> if none is registered.</summary>
    public static IRowMapperFactory<T>? Factory => Volatile.Read(ref s_factory);

    /// <summary>Registers the factory used for <typeparamref name="T"/>. Later registrations replace earlier ones.</summary>
    public static void Register(IRowMapperFactory<T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Volatile.Write(ref s_factory, factory);
    }
}
