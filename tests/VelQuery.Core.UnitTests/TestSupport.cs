using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Common;
using System.Threading;
using VelQuery.Core.UnitTests.Fakes;
using VelQuery.Mapping;
using VelQuery.Parameters;

namespace VelQuery.Core.UnitTests;

/// <summary>A fake database plus a data source wired to it.</summary>
internal sealed class Harness
{
    public Harness(VelQueryOptions? options = null)
    {
        DataSource = new VelQueryDataSource(Provider, new FakeConnectionFactory(Db), options);
    }

    public FakeDatabase Db { get; } = new();

    public FakeProvider Provider { get; } = new();

    public VelQueryDataSource DataSource { get; }

    public static Harness WithFallback(bool strict = true)
        => new(new VelQueryOptions { AllowDynamicFallback = true, StrictMapping = strict });

    public VelQuerySession Session() => DataSource.CreateSession();

    public FakeScript Script(string sql, params FakeResultSet[] results) => Db.Script(sql, results);
}

internal static class Descriptors
{
    public static QueryDescriptor<NoArgs, T> Scalar<T>(string sql, string queryId = "test.scalar", TimeSpan? timeout = null)
        => new(queryId, sql, ScalarRowMapperFactory<T>.Instance, commandTimeout: timeout);

    public static QueryDescriptor<NoArgs, T> Reflection<T>(string sql, string queryId = "test.rows")
        where T : class
        => new(queryId, sql, ReflectionRowMapperFactory<T>.Instance);

    public static readonly ParameterBinder<int> BindId =
        static (command, provider, id) => ParameterWriter.Add(command, provider, new ParameterSpec("Id", System.Data.DbType.Int32), id);
}

/// <summary>Scalar mapper that throws when asked to map the <c>failAtRow</c>-th row (1-based).</summary>
internal sealed class ThrowingAtRowFactory(int failAtRow) : IRowMapperFactory<int>
{
    private readonly int _failAt = failAtRow;
    private int _mapped;

    public int Mapped => Volatile.Read(ref _mapped);

    public IRowMapper<int> Create(DbDataReader reader, in MappingContext context) => new Mapper(this);

    private sealed class Mapper(ThrowingAtRowFactory owner) : IRowMapper<int>
    {
        public int Map(DbDataReader reader)
        {
            if (Interlocked.Increment(ref owner._mapped) == owner._failAt)
            {
                throw new InvalidOperationException($"mapper failed at row {owner._failAt}");
            }

            return reader.GetInt32(0);
        }
    }
}

public sealed class PersonClass
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }
}

public sealed record PersonRecord(int Id, string Name, string? Email);

public sealed class AliasedPerson
{
    [Column("person_id")]
    public int Id { get; set; }

    [Column("full_name")]
    public string? Name { get; set; }
}

public sealed record AliasedRecord([property: Column("person_id")] int Id, string? Name);

public sealed class RequiredPerson
{
    public required int Id { get; set; }

    public string? Note { get; set; }
}

public enum Status
{
    Active = 1,
    Disabled = 2,
}

public sealed class StatusRow
{
    public int Id { get; set; }

    public Status Status { get; set; }

    public Status? Previous { get; set; }
}

public sealed class LooseRow
{
    public int Id { get; set; }

    public int Count { get; set; }
}

/// <summary>Never registered with <see cref="RowMappers{T}"/>.</summary>
public sealed class UnregisteredRow
{
    public int Id { get; set; }
}
