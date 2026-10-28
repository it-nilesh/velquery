using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Mapping;
using VelQuery.Parameters;
using Xunit;

namespace VelQuery.SqlServer.IntegrationTests;

/// <summary>Base for tests sharing the assembly-wide SQL Server fixture.</summary>
public abstract class IntegrationTest(SqlServerFixture fixture)
{
    protected SqlServerFixture Fixture { get; } = fixture;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected VelQueryDataSource DataSource(VelQueryOptions? options = null) => Fixture.CreateDataSource(options);

    protected VelQuerySession Session(VelQueryOptions? options = null) => Fixture.CreateDataSource(options).CreateSession();

    /// <summary>Creates a uniquely named table in the test database and returns its name.</summary>
    protected async Task<string> CreateTableAsync(string columns)
    {
        var table = "dbo.t_" + Guid.NewGuid().ToString("N");
        await using var session = Session();
        await session.ExecuteAsync($"CREATE TABLE {table} ({columns})", cancellationToken: Ct);
        return table;
    }

    /// <summary>A data source with its own small SqlClient pool so leaks surface as pool timeouts.</summary>
    protected VelQueryDataSource IsolatedPoolDataSource(int maxPoolSize, string poolTag, int connectTimeoutSeconds = 5)
        => VelQuerySqlServer.CreateDataSource(
            Fixture.ConnectionStringWith(b =>
            {
                b.MaxPoolSize = maxPoolSize;
                b.ConnectTimeout = connectTimeoutSeconds;
                b.ApplicationName = "VelQuery.It." + poolTag + "." + Guid.NewGuid().ToString("N");
            }),
            new VelQueryOptions { AllowDynamicFallback = true });
}

public sealed record IdName(long Id, string Name);

public sealed record IdCategory(long Id, int Category);

public sealed class TypesRow
{
    public int Id { get; set; }

    public string? LongText { get; set; }

    public string? ShortText { get; set; }

    public string? AnsiText { get; set; }

    public decimal? Amount { get; set; }

    public DateTime? Created { get; set; }

    public DateTimeOffset? CreatedOffset { get; set; }

    public Guid? UniqueId { get; set; }

    public byte[]? Data { get; set; }

    public bool? Flag { get; set; }

    public byte? Tiny { get; set; }

    public short? Small { get; set; }

    public long? Big { get; set; }
}

/// <summary>
/// Hand-written row mapper factory standing in for generated code: resolves ordinals by name once per reader shape,
/// fails on missing columns, ignores extra columns and converts values through <see cref="ValueConverter"/>.
/// </summary>
public sealed class ColumnMapperFactory<T>(string[] columns, Func<ColumnReader, T> map) : IRowMapperFactory<T>
{
    public IRowMapper<T> Create(DbDataReader reader, in MappingContext context)
    {
        var ordinals = new int[columns.Length];
        for (var c = 0; c < columns.Length; c++)
        {
            ordinals[c] = -1;
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (string.Equals(reader.GetName(i), columns[c], StringComparison.OrdinalIgnoreCase))
                {
                    ordinals[c] = i;
                    break;
                }
            }

            if (ordinals[c] < 0)
            {
                throw VelQueryMappingException.MissingColumn(context.QueryId, columns[c], typeof(T));
            }
        }

        return new Mapper(new ColumnReader(columns, ordinals, context), map);
    }

    private sealed class Mapper(ColumnReader columnReader, Func<ColumnReader, T> map) : IRowMapper<T>
    {
        public T Map(DbDataReader reader)
        {
            columnReader.Reader = reader;
            return map(columnReader);
        }
    }
}

public sealed class ColumnReader(string[] columns, int[] ordinals, MappingContext context)
{
    public DbDataReader Reader { get; set; } = null!;

    /// <summary>Reads column <paramref name="index"/> (position in the declared column list).</summary>
    public T Get<T>(int index)
        => ValueConverter.Convert<T>(Reader.GetValue(ordinals[index]), context.QueryId, columns[index], context.StrictNulls);
}

public static class Mappers
{
    public static readonly ColumnMapperFactory<IdName> IdName =
        new(["Id", "Name"], r => new IdName(r.Get<long>(0), r.Get<string>(1)));

    public static readonly ColumnMapperFactory<IdCategory> IdCategory =
        new(["Id", "Category"], r => new IdCategory(r.Get<long>(0), r.Get<int>(1)));

    public static readonly ColumnMapperFactory<TypesRow> Types = new(
        ["Id", "LongText", "ShortText", "AnsiText", "Amount", "Created", "CreatedOffset", "UniqueId", "Data", "Flag", "Tiny", "Small", "Big"],
        r => new TypesRow
        {
            Id = r.Get<int>(0),
            LongText = r.Get<string?>(1),
            ShortText = r.Get<string?>(2),
            AnsiText = r.Get<string?>(3),
            Amount = r.Get<decimal?>(4),
            Created = r.Get<DateTime?>(5),
            CreatedOffset = r.Get<DateTimeOffset?>(6),
            UniqueId = r.Get<Guid?>(7),
            Data = r.Get<byte[]?>(8),
            Flag = r.Get<bool?>(9),
            Tiny = r.Get<byte?>(10),
            Small = r.Get<short?>(11),
            Big = r.Get<long?>(12),
        });
}

public static class Descriptors
{
    public static readonly QueryDescriptor<long, IdName> Rows100ById = new(
        "it.rows100.byId",
        "SELECT Id, Name FROM dbo.Rows100 WHERE Id = @Id",
        Mappers.IdName,
        static (command, provider, id) => ParameterWriter.Add(command, provider, new ParameterSpec("Id", DbType.Int64), id));

    public static readonly QueryDescriptor<long, IdName> Rows100UpTo = new(
        "it.rows100.upTo",
        "SELECT Id, Name FROM dbo.Rows100 WHERE Id <= @Max ORDER BY Id",
        Mappers.IdName,
        static (command, provider, max) => ParameterWriter.Add(command, provider, new ParameterSpec("Max", DbType.Int64), max));

    public static readonly QueryDescriptor<NoArgs, IdName> Rows100kAll = new(
        "it.rows100k.all",
        "SELECT Id, Name FROM dbo.Rows100k ORDER BY Id",
        Mappers.IdName);

    public static readonly QueryDescriptor<int, TypesRow> TypesById = new(
        "it.types.byId",
        "SELECT * FROM dbo.Types WHERE Id = @Id",
        Mappers.Types,
        static (command, provider, id) => ParameterWriter.Add(command, provider, new ParameterSpec("Id", DbType.Int32), id));
}
