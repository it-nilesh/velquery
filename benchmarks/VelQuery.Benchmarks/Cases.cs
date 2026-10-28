using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using VelQuery.Benchmarks.Infrastructure;
using VelQuery.Benchmarks.Libraries;

namespace VelQuery.Benchmarks;

// One class per manifest case (benchmarks/suites/core.yaml). Method names are the manifest's comparison IDs so the
// exported tables read the same as the manifest. Raw ADO.NET is the ratio baseline in every case.

/// <summary>one-row-projection: PK lookup, 6 columns, one int parameter, single-or-default semantics.</summary>
[BenchmarkCategory("one-row-projection")]
public class OneRowProjection : BenchmarkBase
{
    private const int Id = BenchSql.OneRowId;

    [Benchmark(Baseline = true, Description = "raw-adonet")]
    public Task<BenchRow?> RawAdoNet() => Libraries.RawAdoNet.OneRowAsync(ConnectionString, Id);

    [Benchmark(Description = "dapper")]
    public Task<BenchRow?> Dapper() => DapperQueries.OneRowAsync(ConnectionString, Id);

    [Benchmark(Description = "dapper-aot")]
    public Task<BenchRow?> DapperAot() => DapperAotQueries.OneRowAsync(ConnectionString, Id);

    [Benchmark(Description = "velquery-descriptor")]
    public Task<BenchRow?> VelQueryDescriptor() => VelQueryDescriptorQueries.OneRowAsync(DataSource, Id);

    [Benchmark(Description = "velquery-runtime-sql")]
    public Task<BenchRow?> VelQueryRuntimeSql() => VelQueryRuntimeSqlQueries.OneRowAsync(DataSource, Id);

    [Benchmark(Description = "efcore-compiled")]
    public Task<BenchRow?> EfCoreCompiled() => EfCoreQueries.OneRowAsync(EfCore, Id);

    protected override async Task VerifyAsync()
    {
        var expected = Describe(await RawAdoNet());
        Expect(expected, Describe(await Dapper()), "dapper");
        Expect(expected, Describe(await DapperAot()), "dapper-aot");
        Expect(expected, Describe(await VelQueryDescriptor()), "velquery-descriptor");
        Expect(expected, Describe(await VelQueryRuntimeSql()), "velquery-runtime-sql");
        Expect(expected, Describe(await EfCoreCompiled()), "efcore-compiled");
    }
}

/// <summary>scalar-count: <c>SELECT COUNT(*)</c> over a clustered-key range, no parameters.</summary>
[BenchmarkCategory("scalar-count")]
public class ScalarCount : BenchmarkBase
{
    [Benchmark(Baseline = true, Description = "raw-adonet")]
    public Task<int> RawAdoNet() => Libraries.RawAdoNet.ScalarCountAsync(ConnectionString);

    [Benchmark(Description = "dapper")]
    public Task<int> Dapper() => DapperQueries.ScalarCountAsync(ConnectionString);

    [Benchmark(Description = "dapper-aot")]
    public Task<int> DapperAot() => DapperAotQueries.ScalarCountAsync(ConnectionString);

    [Benchmark(Description = "velquery-descriptor")]
    public Task<int> VelQueryDescriptor() => VelQueryDescriptorQueries.ScalarCountAsync(DataSource);

    [Benchmark(Description = "velquery-runtime-sql")]
    public Task<int> VelQueryRuntimeSql() => VelQueryRuntimeSqlQueries.ScalarCountAsync(DataSource);

    [Benchmark(Description = "efcore-compiled")]
    public Task<int> EfCoreCompiled() => EfCoreQueries.ScalarCountAsync(EfCore);

    protected override async Task VerifyAsync()
    {
        var expected = await RawAdoNet();
        Expect(1000, expected, "raw-adonet");
        Expect(expected, await Dapper(), "dapper");
        Expect(expected, await DapperAot(), "dapper-aot");
        Expect(expected, await VelQueryDescriptor(), "velquery-descriptor");
        Expect(expected, await VelQueryRuntimeSql(), "velquery-runtime-sql");
        Expect(expected, await EfCoreCompiled(), "efcore-compiled");
    }
}

/// <summary>buffered-100 / buffered-1000: TOP (n) ordered rows materialized into a list.</summary>
[BenchmarkCategory("buffered")]
public class Buffered : BenchmarkBase
{
    [Params(100, 1000)]
    public int Rows { get; set; }

    private string Sql => BenchSql.Buffered(Rows);

    [Benchmark(Baseline = true, Description = "raw-adonet")]
    public async Task<int> RawAdoNet() => (await Libraries.RawAdoNet.BufferedAsync(ConnectionString, Sql)).Count;

    [Benchmark(Description = "dapper")]
    public async Task<int> Dapper() => (await DapperQueries.BufferedAsync(ConnectionString, Sql)).Count;

    [Benchmark(Description = "dapper-aot")]
    public async Task<int> DapperAot() => (await DapperAotQueries.BufferedAsync(ConnectionString, Sql)).Count;

    [Benchmark(Description = "velquery-descriptor")]
    public async Task<int> VelQueryDescriptor() => (await VelQueryDescriptorQueries.BufferedAsync(DataSource, Rows)).Count;

    [Benchmark(Description = "velquery-runtime-sql")]
    public async Task<int> VelQueryRuntimeSql() => (await VelQueryRuntimeSqlQueries.BufferedAsync(DataSource, Sql)).Count;

    [Benchmark(Description = "efcore-compiled")]
    public async Task<int> EfCoreCompiled() => (await EfCoreQueries.BufferedAsync(EfCore, Rows)).Count;

    protected override async Task VerifyAsync()
    {
        var expected = Fingerprint(await Libraries.RawAdoNet.BufferedAsync(ConnectionString, Sql));
        Expect(expected, Fingerprint(await DapperQueries.BufferedAsync(ConnectionString, Sql)), "dapper");
        Expect(expected, Fingerprint(await DapperAotQueries.BufferedAsync(ConnectionString, Sql)), "dapper-aot");
        Expect(expected, Fingerprint(await VelQueryDescriptorQueries.BufferedAsync(DataSource, Rows)), "velquery-descriptor");
        Expect(expected, Fingerprint(await VelQueryRuntimeSqlQueries.BufferedAsync(DataSource, Sql)), "velquery-runtime-sql");
        Expect(expected, Fingerprint(await EfCoreQueries.BufferedAsync(EfCore, Rows)), "efcore-compiled");
    }

    private static string Fingerprint(IEnumerable<BenchRow> rows) => string.Join('\n', rows.Select(Describe));
}

/// <summary>stream-100k: 100,000 rows consumed one at a time (unbuffered) and folded into a checksum.</summary>
[BenchmarkCategory("stream-100k")]
[WarmupCount(2)]
[IterationCount(5)]
public class Stream100k : BenchmarkBase
{
    [Benchmark(Baseline = true, Description = "raw-adonet")]
    public Task<long> RawAdoNet() => Libraries.RawAdoNet.StreamAsync(ConnectionString);

    [Benchmark(Description = "dapper")]
    public Task<long> Dapper() => DapperQueries.StreamAsync(ConnectionString);

    [Benchmark(Description = "dapper-aot")]
    public Task<long> DapperAot() => DapperAotQueries.StreamAsync(ConnectionString);

    [Benchmark(Description = "velquery-descriptor")]
    public Task<long> VelQueryDescriptor() => VelQueryDescriptorQueries.StreamAsync(DataSource);

    [Benchmark(Description = "velquery-runtime-sql")]
    public Task<long> VelQueryRuntimeSql() => VelQueryRuntimeSqlQueries.StreamAsync(DataSource);

    [Benchmark(Description = "efcore-compiled")]
    public Task<long> EfCoreCompiled() => EfCoreQueries.StreamAsync(EfCore);

    protected override async Task VerifyAsync()
    {
        const long expected = (long)BenchDatabase.RowCount * (BenchDatabase.RowCount + 1) / 2;
        Expect(expected, await RawAdoNet(), "raw-adonet");
        Expect(expected, await Dapper(), "dapper");
        Expect(expected, await DapperAot(), "dapper-aot");
        Expect(expected, await VelQueryDescriptor(), "velquery-descriptor");
        Expect(expected, await VelQueryRuntimeSql(), "velquery-runtime-sql");
        Expect(expected, await EfCoreCompiled(), "efcore-compiled");
    }
}

/// <summary>
/// typed-params: five typed parameters (int, nvarchar(100) with explicit size, decimal(18,4) with explicit
/// precision/scale, datetime2, bit) matching one row.
/// </summary>
[BenchmarkCategory("typed-params")]
public class TypedParams : BenchmarkBase
{
    [Benchmark(Baseline = true, Description = "raw-adonet")]
    public Task<BenchRow?> RawAdoNet() => Libraries.RawAdoNet.TypedParamsAsync(ConnectionString, TypedValues);

    [Benchmark(Description = "dapper")]
    public Task<BenchRow?> Dapper() => DapperQueries.TypedParamsAsync(ConnectionString, TypedValues);

    [Benchmark(Description = "dapper-aot")]
    public Task<BenchRow?> DapperAot() => DapperAotQueries.TypedParamsAsync(ConnectionString, TypedValues);

    [Benchmark(Description = "velquery-descriptor")]
    public Task<BenchRow?> VelQueryDescriptor() => VelQueryDescriptorQueries.TypedParamsAsync(DataSource, TypedValues);

    [Benchmark(Description = "velquery-runtime-sql")]
    public Task<BenchRow?> VelQueryRuntimeSql() => VelQueryRuntimeSqlQueries.TypedParamsAsync(DataSource, TypedValues);

    [Benchmark(Description = "efcore-compiled")]
    public Task<BenchRow?> EfCoreCompiled() => EfCoreQueries.TypedParamsAsync(EfCore, TypedValues);

    protected override async Task VerifyAsync()
    {
        var expected = Describe(await RawAdoNet());
        Expect(Describe(await Libraries.RawAdoNet.OneRowAsync(ConnectionString, BenchSql.OneRowId)), expected, "raw-adonet (typed-params must match the seeded row)");
        Expect(expected, Describe(await Dapper()), "dapper");
        Expect(expected, Describe(await DapperAot()), "dapper-aot");
        Expect(expected, Describe(await VelQueryDescriptor()), "velquery-descriptor");
        Expect(expected, Describe(await VelQueryRuntimeSql()), "velquery-runtime-sql");
        Expect(expected, Describe(await EfCoreCompiled()), "efcore-compiled");
    }
}
