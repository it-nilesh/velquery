using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;

namespace VelQuery.Benchmarks.Libraries;

// Dapper.AOT intercepts Dapper call sites wherever it is enabled. The two classes below are deliberately identical
// call sites with opposite [DapperAot] scopes so the plain-Dapper baseline stays on Dapper's reflection/IL-emit path.
// Verify after a build: obj/<cfg>/net10.0/generated/Dapper.AOT.Analyzers/**/*.cs must contain interceptors only for
// DapperAotQueries (see benchmarks/README.md, "Verifying the Dapper / Dapper.AOT split").

/// <summary>Plain Dapper (runtime reflection + IL emit, cached per query identity). Not intercepted.</summary>
[DapperAot(false)]
public static class DapperQueries
{
    public static async Task<BenchRow?> OneRowAsync(string connectionString, int id)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<BenchRow>(BenchSql.OneRow, new { Id = id });
    }

    public static async Task<int> ScalarCountAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<int>(BenchSql.ScalarCount);
    }

    public static async Task<List<BenchRow>> BufferedAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        // Buffered QueryAsync materializes a List<T> and returns it typed as IEnumerable<T>; no second copy is made.
        var rows = await connection.QueryAsync<BenchRow>(sql);
        return rows as List<BenchRow> ?? rows.ToList();
    }

    public static async Task<long> StreamAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        long checksum = 0;
        await foreach (var row in connection.QueryUnbufferedAsync<BenchRow>(BenchSql.All))
        {
            checksum += row.Id;
        }

        return checksum;
    }

    public static async Task<BenchRow?> TypedParamsAsync(string connectionString, TypedParamValues v)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        // DynamicParameters is plain Dapper's only way to declare size/precision/scale/DbType explicitly.
        var parameters = new DynamicParameters();
        parameters.Add("Id", v.Id, DbType.Int32);
        parameters.Add("Name", v.Name, DbType.String, size: BenchSql.NameSize);
        parameters.Add("Amount", v.Amount, DbType.Decimal, precision: BenchSql.AmountPrecision, scale: BenchSql.AmountScale);
        parameters.Add("CreatedUtc", v.CreatedUtc, DbType.DateTime2);
        parameters.Add("IsActive", v.IsActive, DbType.Boolean);
        return await connection.QuerySingleOrDefaultAsync<BenchRow>(BenchSql.TypedParams, parameters);
    }
}

/// <summary>Dapper.AOT: identical Dapper call sites, replaced at build time by generated interceptors.</summary>
[DapperAot(true)]
public static class DapperAotQueries
{
    public static async Task<BenchRow?> OneRowAsync(string connectionString, int id)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<BenchRow>(BenchSql.OneRow, new { Id = id });
    }

    public static async Task<int> ScalarCountAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<int>(BenchSql.ScalarCount);
    }

    public static async Task<List<BenchRow>> BufferedAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var rows = await connection.QueryAsync<BenchRow>(sql);
        return rows as List<BenchRow> ?? rows.ToList();
    }

    public static async Task<long> StreamAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        long checksum = 0;
        await foreach (var row in connection.QueryUnbufferedAsync<BenchRow>(BenchSql.All))
        {
            checksum += row.Id;
        }

        return checksum;
    }

    public static async Task<BenchRow?> TypedParamsAsync(string connectionString, TypedParamValues v)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        // Dapper.AOT reads [DbValue] metadata from a named parameter type (DynamicParameters is not AOT-supported).
        var args = new DapperAotTypedArgs
        {
            Id = v.Id,
            Name = v.Name,
            Amount = v.Amount,
            CreatedUtc = v.CreatedUtc,
            IsActive = v.IsActive,
        };
        return await connection.QuerySingleOrDefaultAsync<BenchRow>(BenchSql.TypedParams, args);
    }
}

/// <summary>Typed-params arguments carrying explicit parameter metadata for Dapper.AOT.</summary>
public sealed class DapperAotTypedArgs
{
    [DbValue(DbType = DbType.Int32)]
    public int Id { get; set; }

    [DbValue(DbType = DbType.String, Size = BenchSql.NameSize)]
    public string Name { get; set; } = "";

    [DbValue(DbType = DbType.Decimal, Precision = BenchSql.AmountPrecision, Scale = BenchSql.AmountScale)]
    public decimal Amount { get; set; }

    [DbValue(DbType = DbType.DateTime2)]
    public DateTime CreatedUtc { get; set; }

    [DbValue(DbType = DbType.Boolean)]
    public bool IsActive { get; set; }
}
