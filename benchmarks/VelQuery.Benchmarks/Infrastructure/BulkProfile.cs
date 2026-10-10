using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using VelQuery.SqlServer;
using VelQuery.SqlServer.Bulk;

namespace VelQuery.Benchmarks.Infrastructure;

/// <summary>
/// <c>--bulk-profile</c>: write throughput for 1M rows (PRD §14 I/O). Compares one-row-per-round-trip INSERTs with
/// raw <see cref="SqlBulkCopy"/> and VelQuery's <c>BulkInsertAsync</c>, reporting wall time, rows/s, client CPU and
/// allocations. Each variant starts from an empty table.
/// </summary>
public static class BulkProfile
{
    private const int Rows = 1_000_000;
    private const string Table = "dbo.BulkBench";

    public sealed record Item(int Id, string Name, decimal Amount, DateTime CreatedUtc, bool IsActive);

    private static readonly SqlBulkInsertPlan<Item> Plan = SqlBulkInsertPlan<Item>.Create(Table)
        .Column("Id", r => r.Id)
        .Column("Name", r => r.Name)
        .Column("Amount", r => r.Amount)
        .Column("CreatedUtc", r => r.CreatedUtc)
        .Column("IsActive", r => r.IsActive);

    private static IEnumerable<Item> Items(int count, int start = 1)
    {
        var created = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
        for (var i = start; i < start + count; i++)
        {
            yield return new Item(i, "name-" + i, i * 0.01m, created, (i & 1) == 0);
        }
    }

    public static async Task RunAsync(string connectionString)
    {
        var dataSource = VelQuerySqlServer.CreateDataSource(connectionString);
        var noTimeout = new SqlBulkInsertOptions { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        var rawDefaults = noTimeout with { Atomic = false, CheckConstraints = false, KeepNulls = false };
        var variants = new (string Name, int Rows, Func<int, Task<long>> Run)[]
        {
            ("single-row INSERT (1 conn, autocommit)", 5_000, async n =>
            {
                await using var session = dataSource.CreateSession();
                foreach (var item in Items(n))
                {
                    await session.ExecuteAsync(
                        $"INSERT INTO {Table} (Id, Name, Amount, CreatedUtc, IsActive) VALUES (@Id, @Name, @Amount, @CreatedUtc, @IsActive)",
                        new QueryParameters().Add("Id", item.Id).Add("Name", item.Name, DbType.String, size: 100)
                            .Add("Amount", item.Amount, DbType.Decimal, precision: 18, scale: 4).Add("CreatedUtc", item.CreatedUtc, DbType.DateTime2).Add("IsActive", item.IsActive));
                }

                return n;
            }),
            ("raw SqlBulkCopy (driver defaults)", Rows, n => RawBulkAsync(connectionString, n, SqlBulkCopyOptions.Default)),
            ("velquery BulkInsertAsync (safe defaults)", Rows, async n =>
            {
                await using var session = dataSource.CreateSession();
                return await session.BulkInsertAsync(Plan.WithOptions(noTimeout), Items(n));
            }),
            ("velquery BulkInsertAsync (driver defaults)", Rows, async n =>
            {
                await using var session = dataSource.CreateSession();
                return await session.BulkInsertAsync(Plan.WithOptions(rawDefaults), Items(n));
            }),
            ("raw SqlBulkCopy + TableLock", Rows, n => RawBulkAsync(connectionString, n, SqlBulkCopyOptions.TableLock)),
            ("velquery BulkInsertAsync + TableLock", Rows, async n =>
            {
                await using var session = dataSource.CreateSession();
                return await session.BulkInsertAsync(Plan.WithOptions(rawDefaults with { TableLock = true }), Items(n));
            }),
        };

        var process = Process.GetCurrentProcess();
        Console.WriteLine($"{"variant",-44} {"rows",10} {"wall s",8} {"rows/s",12} {"1M rows",10} {"cpu s",7} {"alloc MB",9}");
        foreach (var (name, rows, run) in variants)
        {
            var samples = new List<(double Wall, double Cpu, double Alloc)>();
            for (var round = 0; round < 3; round++)
            {
                await ResetTableAsync(connectionString);
                GC.Collect();
                process.Refresh();
                var cpu = process.TotalProcessorTime;
                var alloc = GC.GetTotalAllocatedBytes(precise: true);
                var stopwatch = Stopwatch.StartNew();
                var written = await run(rows);
                stopwatch.Stop();
                process.Refresh();
                if (written != rows || await CountAsync(connectionString) != rows)
                {
                    throw new InvalidOperationException($"{name}: expected {rows} rows.");
                }

                samples.Add((stopwatch.Elapsed.TotalSeconds, (process.TotalProcessorTime - cpu).TotalSeconds, (GC.GetTotalAllocatedBytes(precise: true) - alloc) / 1048576.0));
            }

            var median = samples.OrderBy(s => s.Wall).ElementAt(1);
            var rate = rows / median.Wall;
            Console.WriteLine($"{name,-44} {rows,10:N0} {median.Wall,8:F2} {rate,12:N0} {TimeSpan.FromSeconds(Rows / rate),10:hh\\:mm\\:ss} {median.Cpu,7:F2} {median.Alloc,9:F0}");
        }

        await using var cleanup = new SqlConnection(connectionString);
        await cleanup.OpenAsync();
        await using var drop = new SqlCommand($"DROP TABLE IF EXISTS {Table}", cleanup);
        await drop.ExecuteNonQueryAsync();
    }

    private static async Task<long> RawBulkAsync(string connectionString, int rows, SqlBulkCopyOptions options)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var bulk = new SqlBulkCopy(connection, options, null)
        {
            DestinationTableName = Table, BatchSize = 10_000, EnableStreaming = true, BulkCopyTimeout = 0,
        };
        using var table = new DataTable();
        foreach (var (name, type) in new[] { ("Id", typeof(int)), ("Name", typeof(string)), ("Amount", typeof(decimal)), ("CreatedUtc", typeof(DateTime)), ("IsActive", typeof(bool)) })
        {
            table.Columns.Add(name, type);
            bulk.ColumnMappings.Add(name, name);
        }

        // Feed the same rows in 10,000-row chunks so the raw baseline doesn't buffer 1M rows in a DataTable.
        foreach (var chunk in Items(rows).Chunk(10_000))
        {
            table.Rows.Clear();
            foreach (var item in chunk)
            {
                table.Rows.Add(item.Id, item.Name, item.Amount, item.CreatedUtc, item.IsActive);
            }

            await bulk.WriteToServerAsync(table);
        }

        return rows;
    }

    private static async Task ResetTableAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"DROP TABLE IF EXISTS {Table}; CREATE TABLE {Table} (Id int NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL, Amount decimal(18,4) NOT NULL, CreatedUtc datetime2(7) NOT NULL, IsActive bit NOT NULL);",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"SELECT COUNT_BIG(*) FROM {Table}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
