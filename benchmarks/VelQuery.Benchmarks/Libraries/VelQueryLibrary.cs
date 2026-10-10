using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace VelQuery.Benchmarks.Libraries;

// Generated contracts: VelQuery.Generators emits the binders, the BenchRow mapper factory and its RowMappers
// registration at build time, exactly as in an application. Every contract uses the shared BenchSql text.

[VelQuerySql("bench.one-row", BenchSql.OneRow)]
internal static partial class OneRowQuery
{
    public static partial Task<BenchRow?> ExecuteAsync(IVelQuerySession session, int id, CancellationToken cancellationToken = default);
}

[VelQuerySql("bench.scalar-count", BenchSql.ScalarCount, Cardinality = QueryCardinality.Scalar)]
internal static partial class ScalarCountQuery
{
    public static partial Task<int> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("bench.top-100", BenchSql.Top100)]
internal static partial class Top100Query
{
    public static partial Task<IReadOnlyList<BenchRow>> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("bench.top-1000", BenchSql.Top1000)]
internal static partial class Top1000Query
{
    public static partial Task<IReadOnlyList<BenchRow>> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("bench.all", BenchSql.All)]
internal static partial class AllRowsQuery
{
    public static partial IAsyncEnumerable<BenchRow> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("bench.typed-params", BenchSql.TypedParams)]
internal static partial class TypedParamsQuery
{
    public static partial Task<BenchRow?> ExecuteAsync(
        IVelQuerySession session,
        int id,
        [VelQueryParameter(DbType = DbType.String, Size = BenchSql.NameSize)] string name,
        [VelQueryParameter(Precision = BenchSql.AmountPrecision, Scale = BenchSql.AmountScale)] decimal amount,
        [VelQueryParameter(DbType = DbType.DateTime2)] DateTime createdUtc,
        bool isActive,
        CancellationToken cancellationToken = default);
}

/// <summary>VelQuery generated contracts (<c>[VelQuerySql]</c> partial methods).</summary>
public static class VelQueryDescriptorQueries
{
    public static async Task<BenchRow?> OneRowAsync(VelQueryDataSource dataSource, int id)
    {
        await using var session = dataSource.CreateSession();
        return await OneRowQuery.ExecuteAsync(session, id);
    }

    public static async Task<int> ScalarCountAsync(VelQueryDataSource dataSource)
    {
        await using var session = dataSource.CreateSession();
        return await ScalarCountQuery.ExecuteAsync(session);
    }

    public static async Task<IReadOnlyList<BenchRow>> BufferedAsync(VelQueryDataSource dataSource, int rows)
    {
        await using var session = dataSource.CreateSession();
        return rows == 100 ? await Top100Query.ExecuteAsync(session) : await Top1000Query.ExecuteAsync(session);
    }

    public static async Task<long> StreamAsync(VelQueryDataSource dataSource)
    {
        await using var session = dataSource.CreateSession();
        long checksum = 0;
        await foreach (var row in AllRowsQuery.ExecuteAsync(session))
        {
            checksum += row.Id;
        }

        return checksum;
    }

    public static async Task<BenchRow?> TypedParamsAsync(VelQueryDataSource dataSource, TypedParamValues v)
    {
        await using var session = dataSource.CreateSession();
        return await TypedParamsQuery.ExecuteAsync(session, v.Id, v.Name, v.Amount, v.CreatedUtc, v.IsActive);
    }
}

/// <summary>
/// VelQuery runtime SQL: <c>session.QueryAsync&lt;T&gt;(sql, QueryParameters)</c> with the mapper resolved from
/// <see cref="RowMappers{T}"/> (registered by the generated module initializer), i.e. no reflection fallback.
/// </summary>
public static class VelQueryRuntimeSqlQueries
{
    public static async Task<BenchRow?> OneRowAsync(VelQueryDataSource dataSource, int id)
    {
        await using var session = dataSource.CreateSession();
        return await session.QuerySingleOrDefaultAsync<BenchRow>(BenchSql.OneRow, new QueryParameters().Add("Id", id));
    }

    public static async Task<int> ScalarCountAsync(VelQueryDataSource dataSource)
    {
        await using var session = dataSource.CreateSession();
        return await session.ExecuteScalarAsync<int>(BenchSql.ScalarCount);
    }

    public static async Task<IReadOnlyList<BenchRow>> BufferedAsync(VelQueryDataSource dataSource, string sql)
    {
        await using var session = dataSource.CreateSession();
        return await session.QueryAsync<BenchRow>(sql);
    }

    public static async Task<long> StreamAsync(VelQueryDataSource dataSource)
    {
        await using var session = dataSource.CreateSession();
        long checksum = 0;
        await foreach (var row in session.StreamAsync<BenchRow>(BenchSql.All))
        {
            checksum += row.Id;
        }

        return checksum;
    }

    public static async Task<BenchRow?> TypedParamsAsync(VelQueryDataSource dataSource, TypedParamValues v)
    {
        await using var session = dataSource.CreateSession();
        var parameters = new QueryParameters()
            .Add("Id", v.Id)
            .Add("Name", v.Name, DbType.String, size: BenchSql.NameSize)
            .Add("Amount", v.Amount, DbType.Decimal, precision: BenchSql.AmountPrecision, scale: BenchSql.AmountScale)
            .Add("CreatedUtc", v.CreatedUtc, DbType.DateTime2)
            .Add("IsActive", v.IsActive);
        return await session.QuerySingleOrDefaultAsync<BenchRow>(BenchSql.TypedParams, parameters);
    }
}
