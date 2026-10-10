using System.Data.Common;
using VelQuery.Providers;

namespace VelQuery.Parameters;

/// <summary>
/// Binds typed arguments to a freshly created command. Generated binders create parameters directly with known
/// metadata; they must not cache or reuse <see cref="DbParameter"/> instances across invocations.
/// </summary>
public delegate void ParameterBinder<TArgs>(DbCommand command, IVelQueryProvider provider, TArgs args);

/// <summary>Helpers used by generated and hand-written binders.</summary>
public static class ParameterWriter
{
    /// <summary>
    /// Creates a parameter described by <paramref name="spec"/>, assigns <paramref name="value"/> (null becomes
    /// <see cref="System.DBNull.Value"/> with type metadata retained) and adds it to the command.
    /// </summary>
    public static DbParameter Add(DbCommand command, IVelQueryProvider provider, in ParameterSpec spec, object? value)
    {
        System.ArgumentNullException.ThrowIfNull(command);
        System.ArgumentNullException.ThrowIfNull(provider);
        var parameter = command.CreateParameter();
        parameter.ParameterName = provider.FormatParameterName(spec.Name);
        parameter.Direction = spec.Direction;
        if (spec.DbType is { } dbType)
        {
            parameter.DbType = dbType;
        }

        if (spec.Size is { } size)
        {
            parameter.Size = size;
        }

        if (spec.Precision is { } precision)
        {
            parameter.Precision = precision;
        }

        if (spec.Scale is { } scale)
        {
            parameter.Scale = scale;
        }

        parameter.Value = value ?? System.DBNull.Value;
        // Last, so providers can apply native types and normalize values (e.g. UTC for PostgreSQL timestamptz).
        provider.ConfigureParameter(parameter, spec, value);
        command.Parameters.Add(parameter);
        return parameter;
    }
}
