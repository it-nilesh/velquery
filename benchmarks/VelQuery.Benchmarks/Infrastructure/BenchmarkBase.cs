using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.SqlClient;
using VelQuery.Benchmarks.Libraries;
using VelQuery.Mapping;
using VelQuery.SqlServer;

namespace VelQuery.Benchmarks.Infrastructure;

/// <summary>
/// Per-process setup shared by every case. The database itself is started and seeded once by the host process;
/// here each benchmark child process only builds its clients and checks that every library returns identical results
/// before anything is measured.
/// </summary>
public abstract class BenchmarkBase
{
    protected string ConnectionString { get; private set; } = "";

    protected VelQueryDataSource DataSource { get; private set; } = null!;

    protected TypedParamValues TypedValues { get; private set; } = null!;

    [GlobalSetup]
    public void GlobalSetup() => SetupAsync().GetAwaiter().GetResult();

    private async Task SetupAsync()
    {
        ConnectionString = Environment.GetEnvironmentVariable(BenchDatabase.ConnectionStringVariable)
            ?? throw new InvalidOperationException(
                $"{BenchDatabase.ConnectionStringVariable} is not set. Run through Program (which starts the container) or set it explicitly.");

        // Logging/diagnostics off; strict mapping on (VelQuery defaults). No dynamic fallback: the runtime-SQL variant
        // must resolve the registered mapper, exactly as generated module initializers will register it.
        DataSource = VelQuerySqlServer.CreateDataSource(ConnectionString, new VelQueryOptions { AllowDynamicFallback = false });
        RowMappers<BenchRow>.Register(BenchRowMapperFactory.Instance);

        TypedValues = await ReadTypedValuesAsync(ConnectionString);
        await VerifyAsync();
    }

    /// <summary>Runs each library once for this case and throws if any result differs from the raw ADO.NET baseline.</summary>
    protected abstract Task VerifyAsync();

    protected static void Expect<T>(T expected, T actual, string library)
    {
        if (!Equals(expected, actual))
        {
            throw new InvalidOperationException($"Result mismatch for {library}: expected {expected}, got {actual}.");
        }
    }

    protected static string Describe(BenchRow? row)
        => row is null ? "<null>" : $"{row.Id}|{row.Name}|{row.Email}|{row.Amount}|{row.CreatedUtc:O}|{row.IsActive}";

    private static async Task<TypedParamValues> ReadTypedValuesAsync(string connectionString)
    {
        var row = await RawAdoNet.OneRowAsync(connectionString, BenchSql.OneRowId)
            ?? throw new InvalidOperationException("Seed row missing; the database was not seeded.");
        return new TypedParamValues(row.Id, row.Name, row.Amount, row.CreatedUtc, row.IsActive);
    }

    [GlobalCleanup]
    public void GlobalCleanup() => SqlConnection.ClearAllPools();
}
