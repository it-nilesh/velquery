using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace VelQuery.Benchmarks.Libraries;

/// <summary>
/// Hand-optimized ADO.NET baseline: SqlClient types directly, typed getters, ordinals fixed by the SELECT list (the
/// cheapest possible "cached" ordinals), explicit parameter metadata, pooled connection opened per operation, and
/// <see cref="CommandBehavior.SequentialAccess"/> (columns are read strictly in order), which Dapper and Dapper.AOT
/// also use.
/// </summary>
public static class RawAdoNet
{
    private const int OId = 0, OName = 1, OEmail = 2, OAmount = 3, OCreated = 4, OActive = 5;

    private const CommandBehavior Behavior = CommandBehavior.SingleResult | CommandBehavior.SequentialAccess;

    public static async Task<BenchRow?> OneRowAsync(string connectionString, int id)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(BenchSql.OneRow, connection);
        command.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = id });
        await using var reader = await command.ExecuteReaderAsync(Behavior);
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var row = Map(reader);
        if (await reader.ReadAsync())
        {
            throw new System.InvalidOperationException("more than one row");
        }

        return row;
    }

    public static async Task<int> ScalarCountAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(BenchSql.ScalarCount, connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    public static async Task<List<BenchRow>> BufferedAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Behavior);
        var rows = new List<BenchRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(Map(reader));
        }

        return rows;
    }

    public static async Task<long> StreamAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(BenchSql.All, connection);
        await using var reader = await command.ExecuteReaderAsync(Behavior);
        long checksum = 0;
        while (await reader.ReadAsync())
        {
            checksum += Map(reader).Id;
        }

        return checksum;
    }

    public static async Task<BenchRow?> TypedParamsAsync(string connectionString, TypedParamValues v)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(BenchSql.TypedParams, connection);
        var p = command.Parameters;
        p.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = v.Id });
        p.Add(new SqlParameter("@Name", SqlDbType.NVarChar, BenchSql.NameSize) { Value = v.Name });
        p.Add(new SqlParameter("@Amount", SqlDbType.Decimal) { Precision = BenchSql.AmountPrecision, Scale = BenchSql.AmountScale, Value = v.Amount });
        p.Add(new SqlParameter("@CreatedUtc", SqlDbType.DateTime2) { Value = v.CreatedUtc });
        p.Add(new SqlParameter("@IsActive", SqlDbType.Bit) { Value = v.IsActive });
        await using var reader = await command.ExecuteReaderAsync(Behavior);
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var row = Map(reader);
        if (await reader.ReadAsync())
        {
            throw new System.InvalidOperationException("more than one row");
        }

        return row;
    }

    private static BenchRow Map(SqlDataReader reader) => new()
    {
        Id = reader.GetInt32(OId),
        Name = reader.GetString(OName),
        Email = reader.IsDBNull(OEmail) ? null : reader.GetString(OEmail),
        Amount = reader.GetDecimal(OAmount),
        CreatedUtc = reader.GetDateTime(OCreated),
        IsActive = reader.GetBoolean(OActive),
    };
}
