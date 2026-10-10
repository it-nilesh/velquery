using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using VelQuery.Parameters;
using VelQuery.Providers;

namespace VelQuery;

/// <summary>
/// Explicit, typed parameters for runtime SQL. Values are always bound as driver parameters and never interpolated
/// into SQL text (VQ-FR-007, VQ-SEC-001). Not thread-safe: do not share an instance between concurrent executions.
/// </summary>
public sealed class QueryParameters
{
    private readonly List<Entry> _entries = [];

    /// <summary>Number of parameters.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Adds an input parameter. The <see cref="DbType"/> is inferred from <typeparamref name="T"/> (or the runtime type
    /// when <typeparamref name="T"/> is <see cref="object"/>) unless supplied. Supply <paramref name="size"/>,
    /// <paramref name="precision"/> and <paramref name="scale"/> for strings and decimals to keep plans stable.
    /// </summary>
    public QueryParameters Add<T>(string name, T value, DbType? dbType = null, int? size = null, byte? precision = null, byte? scale = null)
    {
        var type = typeof(T) == typeof(object) ? value?.GetType() ?? typeof(object) : typeof(T);
        return Add(new ParameterSpec(Normalize(name), dbType ?? DbTypeInference.Infer(type), size, precision, scale), value);
    }

    /// <summary>Adds a parameter described by <paramref name="spec"/>.</summary>
    public QueryParameters Add(in ParameterSpec spec, object? value)
    {
        var normalized = spec with { Name = Normalize(spec.Name) };
        EnsureUnique(normalized.Name);
        _entries.Add(new Entry(normalized, value, null));
        return this;
    }

    /// <summary>
    /// Adds an output or input/output parameter. Its value is available from the returned handle after the command
    /// completes (for readers: after all results are consumed).
    /// </summary>
    public OutputParameter<T> AddOutput<T>(string name, DbType? dbType = null, int? size = null, byte? precision = null, byte? scale = null,
        ParameterDirection direction = ParameterDirection.Output, T? inputValue = default)
    {
        if (direction is not (ParameterDirection.Output or ParameterDirection.InputOutput or ParameterDirection.ReturnValue))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), "direction must be Output, InputOutput or ReturnValue.");
        }

        var spec = new ParameterSpec(Normalize(name), dbType ?? DbTypeInference.Infer(typeof(T)), size, precision, scale, direction);
        EnsureUnique(spec.Name);
        var output = new OutputParameter<T>(spec.Name);
        _entries.Add(new Entry(spec, direction == ParameterDirection.InputOutput ? inputValue : null, output));
        return output;
    }

    /// <summary>
    /// Creates parameters from the public readable properties of <paramref name="args"/> (for example an anonymous
    /// object). Uses reflection over annotated members, which is trimming-safe; prefer generated query contracts
    /// on hot paths.
    /// </summary>
    public static QueryParameters FromObject<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(T args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var parameters = new QueryParameters();
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var spec = new ParameterSpec(Normalize(property.Name), DbTypeInference.Infer(property.PropertyType));
            parameters.Add(spec, property.GetValue(args));
        }

        return parameters;
    }

    internal void Bind(DbCommand command, IVelQueryProvider provider)
    {
        foreach (var entry in _entries)
        {
            ParameterWriter.Add(command, provider, entry.Spec, entry.Value);
        }
    }

    internal void Complete(DbCommand command, string? queryId, bool strictNulls)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            _entries[i].Output?.Capture(command.Parameters[i].Value, queryId, strictNulls);
        }
    }

    private void EnsureUnique(string name)
    {
        foreach (var entry in _entries)
        {
            if (string.Equals(entry.Spec.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"A parameter named '{name}' has already been added.", nameof(name));
            }
        }
    }

    internal static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var span = name.AsSpan();
        if (span[0] is '@' or ':' or '$' or '?')
        {
            span = span[1..];
        }

        if (span.IsEmpty || char.IsDigit(span[0]))
        {
            throw new ArgumentException($"'{name}' is not a valid parameter name.", nameof(name));
        }

        foreach (var c in span)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                throw new ArgumentException($"'{name}' is not a valid parameter name.", nameof(name));
            }
        }

        return span.Length == name.Length ? name : span.ToString();
    }

    private readonly record struct Entry(ParameterSpec Spec, object? Value, IOutputSink? Output);
}

internal interface IOutputSink
{
    void Capture(object? value, string? queryId, bool strictNulls);
}

/// <summary>Handle to an output parameter value populated after command completion.</summary>
public sealed class OutputParameter<T> : IOutputSink
{
    private T? _value;

    internal OutputParameter(string name) => Name = name;

    /// <summary>The parameter name.</summary>
    public string Name { get; }

    /// <summary>Whether the command has completed and populated the value.</summary>
    public bool HasValue { get; private set; }

    /// <summary>The output value. Throws if the command has not completed.</summary>
    public T? Value => HasValue ? _value : throw new InvalidOperationException($"Output parameter '{Name}' has not been populated; consume all results first.");

    void IOutputSink.Capture(object? value, string? queryId, bool strictNulls)
    {
        _value = Mapping.ValueConverter.Convert<T?>(value, queryId, Name, strictNulls);
        HasValue = true;
    }
}
