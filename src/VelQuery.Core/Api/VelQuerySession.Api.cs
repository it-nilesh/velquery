using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Execution;
using VelQuery.Mapping;

namespace VelQuery;

public sealed partial class VelQuerySession
{
    private const DynamicallyAccessedMemberTypes Members = ReflectionRowMapperFactory.RequiredMembers;

    // ---- Compiled descriptors (generated, reflection-free path) ----

    /// <inheritdoc />
    public Task<IReadOnlyList<TRow>> QueryAsync<TArgs, TRow>(QueryDescriptor<TArgs, TRow> query, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return BufferAsync(new DescriptorSpec<TArgs>(query, args), query.Mapper, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TRow> QueryFirstAsync<TArgs, TRow>(QueryDescriptor<TArgs, TRow> query, TArgs args, CancellationToken cancellationToken = default)
        => One(query, args, RowCardinality.First, cancellationToken)!;

    /// <inheritdoc />
    public Task<TRow?> QueryFirstOrDefaultAsync<TArgs, TRow>(QueryDescriptor<TArgs, TRow> query, TArgs args, CancellationToken cancellationToken = default)
        => One(query, args, RowCardinality.FirstOrDefault, cancellationToken);

    /// <inheritdoc />
    public Task<TRow> QuerySingleAsync<TArgs, TRow>(QueryDescriptor<TArgs, TRow> query, TArgs args, CancellationToken cancellationToken = default)
        => One(query, args, RowCardinality.Single, cancellationToken)!;

    /// <inheritdoc />
    public Task<TRow?> QuerySingleOrDefaultAsync<TArgs, TRow>(QueryDescriptor<TArgs, TRow> query, TArgs args, CancellationToken cancellationToken = default)
        => One(query, args, RowCardinality.SingleOrDefault, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<TRow> StreamAsync<TArgs, TRow>(QueryDescriptor<TArgs, TRow> query, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new QueryStream<DescriptorSpec<TArgs>, TRow>(this, new DescriptorSpec<TArgs>(query, args), query.Mapper, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> ExecuteAsync<TArgs>(QueryDescriptor<TArgs> command, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteNonQueryAsync(new DescriptorSpec<TArgs>(command, args), cancellationToken);
    }

    /// <inheritdoc />
    public Task<TResult> ExecuteScalarAsync<TArgs, TResult>(QueryDescriptor<TArgs, TResult> query, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ExecuteScalarCoreAsync<DescriptorSpec<TArgs>, TResult>(new DescriptorSpec<TArgs>(query, args), cancellationToken);
    }

    /// <inheritdoc />
    public Task<VelQueryMultipleReader> QueryMultipleAsync<TArgs>(QueryDescriptor<TArgs> query, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return OpenMultipleAsync(new DescriptorSpec<TArgs>(query, args), cancellationToken);
    }

    private Task<TRow?> One<TArgs, TRow>(QueryDescriptor<TArgs, TRow> query, TArgs args, RowCardinality cardinality, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ReadOneAsync(new DescriptorSpec<TArgs>(query, args), query.Mapper, cardinality, cancellationToken);
    }

    // ---- Runtime SQL (explicit parameters; mapper from registry or opt-in fallback) ----

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> QueryAsync<[DynamicallyAccessedMembers(Members)] T>(string sql, QueryParameters? parameters = null, CancellationToken cancellationToken = default)
        => Sql(sql, parameters).QueryAsync<T>(cancellationToken);

    /// <inheritdoc />
    public Task<T> QueryFirstAsync<[DynamicallyAccessedMembers(Members)] T>(string sql, QueryParameters? parameters = null, CancellationToken cancellationToken = default)
        => Sql(sql, parameters).QueryFirstAsync<T>(cancellationToken);

    /// <inheritdoc />
    public Task<T?> QueryFirstOrDefaultAsync<[DynamicallyAccessedMembers(Members)] T>(string sql, QueryParameters? parameters = null, CancellationToken cancellationToken = default)
        => Sql(sql, parameters).QueryFirstOrDefaultAsync<T>(cancellationToken);

    /// <inheritdoc />
    public Task<T> QuerySingleAsync<[DynamicallyAccessedMembers(Members)] T>(string sql, QueryParameters? parameters = null, CancellationToken cancellationToken = default)
        => Sql(sql, parameters).QuerySingleAsync<T>(cancellationToken);

    /// <inheritdoc />
    public Task<T?> QuerySingleOrDefaultAsync<[DynamicallyAccessedMembers(Members)] T>(string sql, QueryParameters? parameters = null, CancellationToken cancellationToken = default)
        => Sql(sql, parameters).QuerySingleOrDefaultAsync<T>(cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<T> StreamAsync<[DynamicallyAccessedMembers(Members)] T>(string sql, QueryParameters? parameters = null, CancellationToken cancellationToken = default)
        => Sql(sql, parameters).StreamAsync<T>(cancellationToken);

    /// <inheritdoc />
    public Task<int> ExecuteAsync(string sql, QueryParameters? parameters = null, CancellationToken cancellationToken = default)
        => Sql(sql, parameters).ExecuteAsync(cancellationToken);

    /// <inheritdoc />
    public Task<T> ExecuteScalarAsync<T>(string sql, QueryParameters? parameters = null, CancellationToken cancellationToken = default)
        => Sql(sql, parameters).ExecuteScalarAsync<T>(cancellationToken);

    /// <inheritdoc />
    public Task<VelQueryMultipleReader> QueryMultipleAsync(string sql, QueryParameters? parameters = null, CancellationToken cancellationToken = default)
        => Sql(sql, parameters).QueryMultipleAsync(cancellationToken);
}
