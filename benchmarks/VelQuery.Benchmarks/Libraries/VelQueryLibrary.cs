using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using VelQuery.Mapping;
using VelQuery.Parameters;
using VelQuery.Providers;

namespace VelQuery.Benchmarks.Libraries;

/// <summary>
/// Hand-written stand-in for what VelQuery.Generators will emit for <see cref="BenchRow"/>: ordinals resolved once per
/// reader shape in <see cref="Create"/> (case-insensitive <c>GetOrdinal</c>), then typed getters per row.
/// </summary>
public sealed class BenchRowMapperFactory : IRowMapperFactory<BenchRow>
{
    public static readonly BenchRowMapperFactory Instance = new();

    public IRowMapper<BenchRow> Create(DbDataReader reader, in MappingContext context)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new Mapper(
            Ordinal(reader, "Id", context), Ordinal(reader, "Name", context), Ordinal(reader, "Email", context),
            Ordinal(reader, "Amount", context), Ordinal(reader, "CreatedUtc", context), Ordinal(reader, "IsActive", context),
            context);
    }

    private static int Ordinal(DbDataReader reader, string column, in MappingContext context)
    {
        try
        {
            return reader.GetOrdinal(column);
        }
        catch (IndexOutOfRangeException)
        {
            throw VelQueryMappingException.MissingColumn(context.QueryId, column, typeof(BenchRow));
        }
    }

    private sealed class Mapper(int id, int name, int email, int amount, int created, int active, MappingContext context) : IRowMapper<BenchRow>
    {
        public BenchRow Map(DbDataReader reader)
        {
            if (reader.IsDBNull(name))
            {
                throw VelQueryMappingException.NullToNonNullable(context.QueryId, "Name", typeof(string));
            }

            return new BenchRow
            {
                Id = reader.GetInt32(id),
                Name = reader.GetString(name),
                Email = reader.IsDBNull(email) ? null : reader.GetString(email),
                Amount = reader.GetDecimal(amount),
                CreatedUtc = reader.GetDateTime(created),
                IsActive = reader.GetBoolean(active),
            };
        }
    }
}

/// <summary>Static descriptors as a generator would emit them: immutable, shared, binder delegates cached statically.</summary>
public static class VelQueryDescriptors
{
    public static readonly QueryDescriptor<int, BenchRow> OneRow = new(
        "bench.one-row", BenchSql.OneRow, BenchRowMapperFactory.Instance,
        static (command, provider, id) => ParameterWriter.Add(command, provider, new ParameterSpec("Id", DbType.Int32), id));

    public static readonly QueryDescriptor<NoArgs, int> ScalarCount = new(
        "bench.scalar-count", BenchSql.ScalarCount, ScalarRowMapperFactory<int>.Instance);

    public static readonly QueryDescriptor<NoArgs, BenchRow> Top100 = new("bench.top-100", BenchSql.Top100, BenchRowMapperFactory.Instance);

    public static readonly QueryDescriptor<NoArgs, BenchRow> Top1000 = new("bench.top-1000", BenchSql.Top1000, BenchRowMapperFactory.Instance);

    public static readonly QueryDescriptor<NoArgs, BenchRow> All = new("bench.all", BenchSql.All, BenchRowMapperFactory.Instance);

    public static readonly QueryDescriptor<TypedParamValues, BenchRow> TypedParams = new(
        "bench.typed-params", BenchSql.TypedParams, BenchRowMapperFactory.Instance, BindTyped);

    public static QueryDescriptor<NoArgs, BenchRow> Buffered(int rows) => rows == 100 ? Top100 : Top1000;

    private static void BindTyped(DbCommand command, IVelQueryProvider provider, TypedParamValues v)
    {
        ParameterWriter.Add(command, provider, new ParameterSpec("Id", DbType.Int32), v.Id);
        ParameterWriter.Add(command, provider, new ParameterSpec("Name", DbType.String, Size: BenchSql.NameSize), v.Name);
        ParameterWriter.Add(command, provider, new ParameterSpec("Amount", DbType.Decimal, Precision: BenchSql.AmountPrecision, Scale: BenchSql.AmountScale), v.Amount);
        ParameterWriter.Add(command, provider, new ParameterSpec("CreatedUtc", DbType.DateTime2), v.CreatedUtc);
        ParameterWriter.Add(command, provider, new ParameterSpec("IsActive", DbType.Boolean), v.IsActive);
    }
}

/// <summary>VelQuery, generated-contract style: <c>QueryDescriptor&lt;TArgs,TRow&gt;</c> + hand-written mapper factory.</summary>
public static class VelQueryDescriptorQueries
{
    public static async Task<BenchRow?> OneRowAsync(VelQueryDataSource dataSource, int id)
    {
        await using var session = dataSource.CreateSession();
        return await session.QuerySingleOrDefaultAsync(VelQueryDescriptors.OneRow, id);
    }

    public static async Task<int> ScalarCountAsync(VelQueryDataSource dataSource)
    {
        await using var session = dataSource.CreateSession();
        return await session.ExecuteScalarAsync(VelQueryDescriptors.ScalarCount, default);
    }

    public static async Task<IReadOnlyList<BenchRow>> BufferedAsync(VelQueryDataSource dataSource, int rows)
    {
        await using var session = dataSource.CreateSession();
        return await session.QueryAsync(VelQueryDescriptors.Buffered(rows), default);
    }

    public static async Task<long> StreamAsync(VelQueryDataSource dataSource)
    {
        await using var session = dataSource.CreateSession();
        long checksum = 0;
        await foreach (var row in session.StreamAsync(VelQueryDescriptors.All, default))
        {
            checksum += row.Id;
        }

        return checksum;
    }

    public static async Task<BenchRow?> TypedParamsAsync(VelQueryDataSource dataSource, TypedParamValues v)
    {
        await using var session = dataSource.CreateSession();
        return await session.QuerySingleOrDefaultAsync(VelQueryDescriptors.TypedParams, v);
    }
}

/// <summary>
/// VelQuery runtime SQL: <c>session.QueryAsync&lt;T&gt;(sql, QueryParameters)</c> with the mapper resolved from
/// <see cref="RowMappers{T}"/> (registered in setup), i.e. no reflection fallback.
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
