using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using VelQuery.Benchmarks.Libraries;
using VelQuery.SqlServer;

namespace VelQuery.Benchmarks.Infrastructure;

/// <summary>
/// <c>--alloc-profile</c>: a coarse allocation-by-type view for the stream-100k case, sampled from the runtime's
/// GC AllocationTick events (one event per ~100 KB allocated, attributed to the type that crossed the threshold).
/// Used to explain allocation differences between libraries; not a benchmark.
/// </summary>
public static class AllocationProfile
{
    public static async Task RunAsync(string connectionString)
    {
        var dataSource = VelQuerySqlServer.CreateDataSource(connectionString);
        var variants = new (string Name, Func<Task<long>> Run)[]
        {
            ("raw-adonet (SequentialAccess)", () => RawAdoNet.StreamAsync(connectionString)),
            ("raw-adonet (CommandBehavior.Default)", () => RawStreamDefaultBehaviorAsync(connectionString)),
            ("dapper-aot", () => DapperAotQueries.StreamAsync(connectionString)),
            ("velquery-descriptor", () => VelQueryDescriptorQueries.StreamAsync(dataSource)),
        };

        foreach (var (name, run) in variants)
        {
            for (var i = 0; i < 3; i++)
            {
                await run(); // warm up JIT, pool and tiering
            }

            using var listener = new AllocationListener();
            var before = GC.GetTotalAllocatedBytes(precise: true);
            const int Runs = 5;
            for (var i = 0; i < Runs; i++)
            {
                await run();
            }

            var perOp = (GC.GetTotalAllocatedBytes(precise: true) - before) / Runs;
            Console.WriteLine($"== {name}: {perOp / 1024.0 / 1024.0:F2} MB/op ({perOp / (double)BenchDatabase.RowCount:F0} B/row)");
            foreach (var (type, bytes) in listener.Snapshot().OrderByDescending(p => p.Value).Take(8))
            {
                Console.WriteLine($"   {bytes / Runs / (double)BenchDatabase.RowCount,7:F1} B/row  {type}");
            }
        }
    }

    /// <summary>Same loop as the raw baseline, but with the behavior VelQuery's executor uses today.</summary>
    private static async Task<long> RawStreamDefaultBehaviorAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(BenchSql.All, connection);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.Default);
        long checksum = 0;
        while (await reader.ReadAsync())
        {
            checksum += reader.GetInt32(0);
            _ = reader.GetString(1);
            _ = reader.IsDBNull(2) ? null : reader.GetString(2);
            _ = reader.GetDecimal(3);
            _ = reader.GetDateTime(4);
            _ = reader.GetBoolean(5);
        }

        return checksum;
    }

    private sealed class AllocationListener : EventListener
    {
        private readonly Dictionary<string, long> _bytes = [];
        private readonly Lock _gate = new();

        public Dictionary<string, long> Snapshot()
        {
            lock (_gate)
            {
                return new Dictionary<string, long>(_bytes);
            }
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x1); // GC keyword
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName is not { } eventName || !eventName.StartsWith("GCAllocationTick", StringComparison.Ordinal)
                || eventData.PayloadNames is null || eventData.Payload is null)
            {
                return;
            }

            var typeIndex = eventData.PayloadNames.IndexOf("TypeName");
            var amountIndex = eventData.PayloadNames.IndexOf("AllocationAmount64");
            if (amountIndex < 0)
            {
                amountIndex = eventData.PayloadNames.IndexOf("AllocationAmount");
            }

            if (typeIndex < 0 || amountIndex < 0)
            {
                return;
            }

            var type = eventData.Payload[typeIndex] as string ?? "?";
            var amount = Convert.ToInt64(eventData.Payload[amountIndex], System.Globalization.CultureInfo.InvariantCulture);
            lock (_gate)
            {
                _bytes[type] = _bytes.GetValueOrDefault(type) + amount;
            }
        }
    }
}
