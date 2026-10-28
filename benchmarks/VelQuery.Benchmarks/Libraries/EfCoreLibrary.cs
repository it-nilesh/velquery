using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace VelQuery.Benchmarks.Libraries;

/// <summary>
/// EF Core in its fastest common configuration, so the comparison is fair to EF: a pooled <see cref="DbContext"/>
/// factory, no change tracking, and compiled queries (no LINQ translation per call). Column types are mapped
/// explicitly so parameters carry the same metadata as the other libraries. EF generates its own SQL (for example
/// <c>SELECT TOP(2)</c> for single-or-default); the manifest's "query-text-identical" control does not apply to it.
/// </summary>
public sealed class BenchContext(DbContextOptions<BenchContext> options) : DbContext(options)
{
    public DbSet<BenchRow> BenchRows => Set<BenchRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var row = modelBuilder.Entity<BenchRow>();
        row.ToTable("BenchRows", "dbo").HasKey(r => r.Id);
        row.Property(r => r.Id).ValueGeneratedNever();
        row.Property(r => r.Name).HasColumnType("nvarchar(100)");
        row.Property(r => r.Email).HasColumnType("nvarchar(200)");
        row.Property(r => r.Amount).HasColumnType("decimal(18,4)");
        row.Property(r => r.CreatedUtc).HasColumnType("datetime2");
        row.Property(r => r.IsActive).HasColumnType("bit");
    }
}

/// <summary>EF Core queries for each benchmark case.</summary>
public static class EfCoreQueries
{
    private static readonly Func<BenchContext, int, Task<BenchRow?>> OneRow = EF.CompileAsyncQuery(
        (BenchContext db, int id) => db.BenchRows.AsNoTracking().SingleOrDefault(r => r.Id == id));

    private static readonly Func<BenchContext, Task<int>> Count = EF.CompileAsyncQuery(
        (BenchContext db) => db.BenchRows.Count(r => r.Id <= 1000));

    private static readonly Func<BenchContext, int, IAsyncEnumerable<BenchRow>> Top = EF.CompileAsyncQuery(
        (BenchContext db, int rows) => db.BenchRows.AsNoTracking().OrderBy(r => r.Id).Take(rows));

    private static readonly Func<BenchContext, IAsyncEnumerable<BenchRow>> All = EF.CompileAsyncQuery(
        (BenchContext db) => db.BenchRows.AsNoTracking().OrderBy(r => r.Id).AsQueryable());

    private static readonly Func<BenchContext, int, string, decimal, DateTime, bool, Task<BenchRow?>> Typed = EF.CompileAsyncQuery(
        (BenchContext db, int id, string name, decimal amount, DateTime created, bool active) => db.BenchRows.AsNoTracking()
            .SingleOrDefault(r => r.Id == id && r.Name == name && r.Amount == amount && r.CreatedUtc == created && r.IsActive == active));

    /// <summary>One pooled factory per process, like a registered <c>AddPooledDbContextFactory</c>.</summary>
    public static PooledDbContextFactory<BenchContext> CreateFactory(string connectionString)
        => new(new DbContextOptionsBuilder<BenchContext>()
            .UseSqlServer(connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options);

    public static async Task<BenchRow?> OneRowAsync(PooledDbContextFactory<BenchContext> factory, int id)
    {
        await using var db = factory.CreateDbContext();
        return await OneRow(db, id);
    }

    public static async Task<int> ScalarCountAsync(PooledDbContextFactory<BenchContext> factory)
    {
        await using var db = factory.CreateDbContext();
        return await Count(db);
    }

    public static async Task<List<BenchRow>> BufferedAsync(PooledDbContextFactory<BenchContext> factory, int rows)
    {
        await using var db = factory.CreateDbContext();
        var list = new List<BenchRow>(rows);
        await foreach (var row in Top(db, rows))
        {
            list.Add(row);
        }

        return list;
    }

    public static async Task<long> StreamAsync(PooledDbContextFactory<BenchContext> factory)
    {
        await using var db = factory.CreateDbContext();
        long checksum = 0;
        await foreach (var row in All(db))
        {
            checksum += row.Id;
        }

        return checksum;
    }

    public static async Task<BenchRow?> TypedParamsAsync(PooledDbContextFactory<BenchContext> factory, TypedParamValues v)
    {
        await using var db = factory.CreateDbContext();
        return await Typed(db, v.Id, v.Name, v.Amount, v.CreatedUtc, v.IsActive);
    }
}
