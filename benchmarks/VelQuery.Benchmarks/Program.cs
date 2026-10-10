using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;
using Microsoft.Data.SqlClient;
using VelQuery.Benchmarks.Infrastructure;

namespace VelQuery.Benchmarks;

public static class Program
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> Main(string[] args)
    {
        var config = ManualConfig.Create(DefaultConfig.Instance)
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddExporter(JsonExporter.Full)
            .AddColumn(StatisticColumn.P95)
            .AddColumn(CategoriesColumn.Default);

        var switcher = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly);
        if (args.Any(a => a is "--list" or "--help" or "-h" or "--version" or "--info"))
        {
            switcher.Run(args, config);
            return 0;
        }

        // One database for the whole run, started before BenchmarkDotNet launches its per-benchmark child processes.
        // Children inherit the connection string through the environment.
        await using var database = await BenchDatabase.StartAsync();
        Console.WriteLine($"[velquery-bench] database: {database.Source}");
        await database.SeedAsync();
        Environment.SetEnvironmentVariable(BenchDatabase.ConnectionStringVariable, database.ConnectionString);

        if (args.Contains("--alloc-profile"))
        {
            await AllocationProfile.RunAsync(database.ConnectionString);
            return 0;
        }

        if (args.Contains("--bulk-profile"))
        {
            await BulkProfile.RunAsync(database.ConnectionString);
            return 0;
        }

        if (args.Contains("--cpu-profile"))
        {
            await CpuProfile.RunAsync(database.ConnectionString);
            return 0;
        }

        var artifacts = ArtifactsPath(args);
        Directory.CreateDirectory(artifacts);
        var environment = await DescribeEnvironmentAsync(database);
        var environmentPath = Path.Combine(artifacts, "environment.json");
        await File.WriteAllTextAsync(environmentPath, JsonSerializer.Serialize(environment, s_json));
        Console.WriteLine($"[velquery-bench] environment written to {environmentPath}");

        var summaries = switcher.Run(args, config.WithArtifactsPath(artifacts)).ToList();
        return summaries.Any(s => s.HasCriticalValidationErrors || s.Reports.Any(r => !r.Success)) ? 1 : 0;
    }

    private static string ArtifactsPath(string[] args)
    {
        var index = Array.IndexOf(args, "--artifacts");
        return index >= 0 && index + 1 < args.Length
            ? Path.GetFullPath(args[index + 1])
            : Path.Combine(Directory.GetCurrentDirectory(), "BenchmarkDotNet.Artifacts");
    }

    private static async Task<Dictionary<string, string>> DescribeEnvironmentAsync(BenchDatabase database)
    {
        var (productVersion, edition, version) = await database.ServerInfoAsync();
        var csb = new SqlConnectionStringBuilder(database.ConnectionString);
        return new Dictionary<string, string>
        {
            ["suite"] = "velquery.query-core.v1",
            ["capturedAtUtc"] = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["os"] = RuntimeInformation.OSDescription,
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["processorCount"] = Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["gc"] = GCSettings.IsServerGC ? "server" : "workstation",
            ["database.source"] = database.Source,
            ["database.productVersion"] = productVersion,
            ["database.edition"] = edition,
            ["database.version"] = version.ReplaceLineEndings(" "),
            ["database.dataSource"] = csb.DataSource,
            ["database.rows"] = BenchDatabase.RowCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["database.seed"] = BenchDatabase.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["pooling"] = $"Pooling={csb.Pooling};Min={csb.MinPoolSize};Max={csb.MaxPoolSize};MARS={csb.MultipleActiveResultSets}",
            ["driver.Microsoft.Data.SqlClient"] = VersionOf(typeof(SqlConnection)),
            ["Dapper"] = VersionOf(typeof(Dapper.SqlMapper)),
            ["Dapper.AOT"] = VersionOf(typeof(Dapper.DapperAotAttribute)),
            ["BenchmarkDotNet"] = VersionOf(typeof(BenchmarkSwitcher)),
            ["VelQuery.Core"] = VersionOf(typeof(VelQueryDataSource)),
        };
    }

    private static string VersionOf(Type type)
        => type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? type.Assembly.GetName().Version?.ToString()
           ?? "unknown";
}
