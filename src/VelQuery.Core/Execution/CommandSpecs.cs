using System;
using System.Data;
using System.Data.Common;
using VelQuery.Providers;

namespace VelQuery.Execution;

/// <summary>Describes one command invocation. Implemented by structs so the executor is specialized without allocation.</summary>
internal interface ICommandSpec
{
    string QueryId { get; }

    string Sql { get; }

    CommandType CommandType { get; }

    TimeSpan? Timeout { get; }

    void Bind(DbCommand command, IVelQueryProvider provider);

    void Complete(DbCommand command, bool strictNulls);
}

internal readonly struct DescriptorSpec<TArgs>(QueryDescriptor<TArgs> descriptor, TArgs args) : ICommandSpec
{
    public string QueryId => descriptor.QueryId;

    public string Sql => descriptor.Sql;

    public CommandType CommandType => descriptor.CommandType;

    public TimeSpan? Timeout => descriptor.CommandTimeout;

    public void Bind(DbCommand command, IVelQueryProvider provider) => descriptor.Binder?.Invoke(command, provider, args);

    public void Complete(DbCommand command, bool strictNulls)
    {
    }
}

internal readonly struct TextSpec(string sql, QueryParameters? parameters, string? queryId, TimeSpan? timeout, CommandType commandType) : ICommandSpec
{
    public string QueryId => queryId ?? QueryIdentity.ForSql(sql);

    public string Sql => sql;

    public CommandType CommandType => commandType;

    public TimeSpan? Timeout => timeout;

    public void Bind(DbCommand command, IVelQueryProvider provider) => parameters?.Bind(command, provider);

    public void Complete(DbCommand command, bool strictNulls) => parameters?.Complete(command, QueryId, strictNulls);
}
