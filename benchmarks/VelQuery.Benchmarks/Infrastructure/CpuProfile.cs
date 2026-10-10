using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using VelQuery.Benchmarks.Libraries;
using VelQuery.SqlServer;

namespace VelQuery.Benchmarks.Infrastructure;

/// <summary>
/// <c>--cpu-profile</c>: client CPU time per operation (PRD §14.2 metric <c>cpu-ms-per-1000-ops</c>). The database
/// runs in a separate process (container), so process CPU here is what the library, driver, runtime and GC cost on
/// the application side. Operations run sequentially; libraries are interleaved across rounds to spread drift, and
/// the median round is reported.
/// </summary>
public static class CpuProfile
{
    private const int Rounds = 5;

    public static async Task RunAsync(string connectionString)
    {
        var dataSource = VelQuerySqlServer.CreateDataSource(connectionString);
        var typed = await BenchmarkBase.ReadTypedValuesAsync(connectionString);
        var cases = new (string Case, int Ops, (string Library, Func<Task> Run)[] Libraries)[]
        {
            ("one-row-projection", 4000,
            [
                ("raw-adonet", () => RawAdoNet.OneRowAsync(connectionString, BenchSql.OneRowId)),
                ("dapper", () => DapperQueries.OneRowAsync(connectionString, BenchSql.OneRowId)),
                ("dapper-aot", () => DapperAotQueries.OneRowAsync(connectionString, BenchSql.OneRowId)),
                ("velquery-descriptor", () => VelQueryDescriptorQueries.OneRowAsync(dataSource, BenchSql.OneRowId)),
                ("velquery-runtime-sql", () => VelQueryRuntimeSqlQueries.OneRowAsync(dataSource, BenchSql.OneRowId)),
            ]),
            ("typed-params", 4000,
            [
                ("raw-adonet", () => RawAdoNet.TypedParamsAsync(connectionString, typed)),
                ("dapper", () => DapperQueries.TypedParamsAsync(connectionString, typed)),
                ("dapper-aot", () => DapperAotQueries.TypedParamsAsync(connectionString, typed)),
                ("velquery-descriptor", () => VelQueryDescriptorQueries.TypedParamsAsync(dataSource, typed)),
                ("velquery-runtime-sql", () => VelQueryRuntimeSqlQueries.TypedParamsAsync(dataSource, typed)),
            ]),
            ("buffered-1000", 300,
            [
                ("raw-adonet", () => RawAdoNet.BufferedAsync(connectionString, BenchSql.Top1000)),
                ("dapper", () => DapperQueries.BufferedAsync(connectionString, BenchSql.Top1000)),
                ("dapper-aot", () => DapperAotQueries.BufferedAsync(connectionString, BenchSql.Top1000)),
                ("velquery-descriptor", () => VelQueryDescriptorQueries.BufferedAsync(dataSource, 1000)),
                ("velquery-runtime-sql", () => VelQueryRuntimeSqlQueries.BufferedAsync(dataSource, BenchSql.Top1000)),
            ]),
            ("stream-100k", 8,
            [
                ("raw-adonet", () => RawAdoNet.StreamAsync(connectionString)),
                ("dapper", () => DapperQueries.StreamAsync(connectionString)),
                ("dapper-aot", () => DapperAotQueries.StreamAsync(connectionString)),
                ("velquery-descriptor", () => VelQueryDescriptorQueries.StreamAsync(dataSource)),
                ("velquery-runtime-sql", () => VelQueryRuntimeSqlQueries.StreamAsync(dataSource)),
            ]),
        };

        var process = Process.GetCurrentProcess();
        Console.WriteLine($"{"case",-20} {"library",-22} {"cpu µs/op",12} {"cpu ms/1000 ops",16} {"wall µs/op",12} {"cpu/raw",8}");
        foreach (var (name, ops, libraries) in cases)
        {
            foreach (var (_, run) in libraries)
            {
                for (var i = 0; i < Math.Min(ops, 200); i++)
                {
                    await run(); // JIT, tiering, pool warm-up
                }
            }

            var cpu = libraries.ToDictionary(l => l.Library, _ => new List<double>());
            var wall = libraries.ToDictionary(l => l.Library, _ => new List<double>());
            for (var round = 0; round < Rounds; round++)
            {
                foreach (var (library, run) in libraries)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    process.Refresh();
                    var cpuBefore = process.TotalProcessorTime;
                    var stopwatch = Stopwatch.StartNew();
                    for (var i = 0; i < ops; i++)
                    {
                        await run();
                    }

                    stopwatch.Stop();
                    process.Refresh();
                    cpu[library].Add((process.TotalProcessorTime - cpuBefore).TotalMicroseconds / ops);
                    wall[library].Add(stopwatch.Elapsed.TotalMicroseconds / ops);
                }
            }

            var rawCpu = Median(cpu["raw-adonet"]);
            foreach (var (library, _) in libraries)
            {
                var c = Median(cpu[library]);
                Console.WriteLine($"{name,-20} {library,-22} {c,12:F1} {c,16:F1} {Median(wall[library]),12:F1} {c / rawCpu,8:F2}");
            }
        }
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}
