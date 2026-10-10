using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Mapping;

namespace VelQuery;

/// <summary>
/// The VelQuery query surface (decision D-01). Descriptor overloads are the generated, reflection-free path; string
/// overloads run runtime SQL with explicit <see cref="QueryParameters"/>.
/// </summary>
public interface IVelQuerySession : IAsyncDisposable
{
    /// <summary>Buffers all rows in reader order; empty results yield an empty list (VQ-FR-002).</summary>
    Task<IReadOnlyList<TRow>> QueryAsync<TArgs, TRow>(QueryDescriptor<TArgs, TRow> query, TArgs args, CancellationToken cancellationToken = default);

    /// <summary>Returns the first row or throws <see cref="VelQueryNoRowsException"/> (VQ-FR-003).</summary>
    Task<TRow> QueryFirstAsync<TArgs, TRow>(QueryDescriptor<TArgs, TRow> query, TArgs args, CancellationToken cancellationToken = default);

    /// <summary>Returns the first row or default.</summary>
    Task<TRow?> QueryFirstOrDefaultAsync<TArgs, TRow>(QueryDescriptor<TArgs, TRow> query, TArgs args, CancellationToken cancellationToken = default);

    /// <summary>Returns the only row; throws for zero or multiple rows (VQ-FR-004).</summary>
    Task<TRow> QuerySingleAsync<TArgs, TRow>(QueryDescriptor<TArgs, TRow> query, TArgs args, CancellationToken cancellationToken = default);

    /// <summary>Returns the only row or default; throws for multiple rows.</summary>
    Task<TRow?> QuerySingleOrDefaultAsync<TArgs, TRow>(QueryDescriptor<TArgs, TRow> query, TArgs args, CancellationToken cancellationToken = default);

    /// <summary>Streams rows; resources are held until enumeration completes or is disposed (VQ-FR-013).</summary>
    IAsyncEnumerable<TRow> StreamAsync<TArgs, TRow>(QueryDescriptor<TArgs, TRow> query, TArgs args, CancellationToken cancellationToken = default);

    /// <summary>Executes a command and returns the provider-reported affected row count (VQ-FR-006).</summary>
    Task<int> ExecuteAsync<TArgs>(QueryDescriptor<TArgs> command, TArgs args, CancellationToken cancellationToken = default);

    /// <summary>Returns the first column of the first row (VQ-FR-005).</summary>
    Task<TResult> ExecuteScalarAsync<TArgs, TResult>(QueryDescriptor<TArgs, TResult> query, TArgs args, CancellationToken cancellationToken = default);

    /// <summary>Opens a reader over multiple result sets (VQ-FR-021).</summary>
    Task<VelQueryMultipleReader> QueryMultipleAsync<TArgs>(QueryDescriptor<TArgs> query, TArgs args, CancellationToken cancellationToken = default);

    /// <summary>Buffers all rows of runtime SQL.</summary>
    Task<IReadOnlyList<T>> QueryAsync<[DynamicallyAccessedMembers(ReflectionRowMapperFactory.RequiredMembers)] T>(string sql, QueryParameters? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>Returns the first row of runtime SQL or throws <see cref="VelQueryNoRowsException"/>.</summary>
    Task<T> QueryFirstAsync<[DynamicallyAccessedMembers(ReflectionRowMapperFactory.RequiredMembers)] T>(string sql, QueryParameters? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>Returns the first row of runtime SQL or default.</summary>
    Task<T?> QueryFirstOrDefaultAsync<[DynamicallyAccessedMembers(ReflectionRowMapperFactory.RequiredMembers)] T>(string sql, QueryParameters? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>Returns the only row of runtime SQL; throws for zero or multiple rows.</summary>
    Task<T> QuerySingleAsync<[DynamicallyAccessedMembers(ReflectionRowMapperFactory.RequiredMembers)] T>(string sql, QueryParameters? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>Returns the only row of runtime SQL or default; throws for multiple rows.</summary>
    Task<T?> QuerySingleOrDefaultAsync<[DynamicallyAccessedMembers(ReflectionRowMapperFactory.RequiredMembers)] T>(string sql, QueryParameters? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>Streams rows of runtime SQL.</summary>
    IAsyncEnumerable<T> StreamAsync<[DynamicallyAccessedMembers(ReflectionRowMapperFactory.RequiredMembers)] T>(string sql, QueryParameters? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>Executes runtime SQL and returns the affected row count.</summary>
    Task<int> ExecuteAsync(string sql, QueryParameters? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>Returns the first column of the first row of runtime SQL.</summary>
    Task<T> ExecuteScalarAsync<T>(string sql, QueryParameters? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>Opens a multi-result reader over runtime SQL.</summary>
    Task<VelQueryMultipleReader> QueryMultipleAsync(string sql, QueryParameters? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>Creates a runtime SQL command with per-call options (timeout, query ID, stored procedure).</summary>
    VelQueryTextCommand Sql(string sql, QueryParameters? parameters = null);

    /// <summary>Begins a session transaction; disposing without commit rolls back.</summary>
    ValueTask<VelQueryTransaction> BeginTransactionAsync(IsolationLevel isolationLevel = IsolationLevel.Unspecified, CancellationToken cancellationToken = default);
}
