using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Core.UnitTests.Fakes;
using Xunit;

namespace VelQuery.Core.UnitTests;

/// <summary>VQ-FR-028 / PRD §9.6: one immutable descriptor shared by 1,000 concurrent invocations.</summary>
public sealed class ParallelDescriptorTests
{
    private const int Invocations = 1000;

    private static readonly QueryDescriptor<int, PersonRecord> ById = new(
        "people.by-id",
        "select Id, Name, Email from people where Id = @Id",
        Mapping.ReflectionRowMapperFactory<PersonRecord>.Instance,
        Descriptors.BindId);

    private static readonly QueryDescriptor<int, int> EchoScalar = new(
        "echo.scalar", "select @Id", Mapping.ScalarRowMapperFactory<int>.Instance, Descriptors.BindId);

    [Fact]
    public async Task Shared_descriptor_is_safe_across_1000_parallel_invocations_on_independent_sessions()
    {
        var db = new FakeDatabase
        {
            // Echo the bound parameter back so that any cross-invocation leakage shows up as a wrong row.
            Responder = command =>
            {
                var id = (int)command.BoundParameters.Single().Value!;
                return new FakeScript().With(new FakeResultSet(("Id", typeof(int)), ("Name", typeof(string)), ("Email", typeof(string)))
                    .Row(id, "n" + id, null));
            },
        };
        var dataSource = new VelQueryDataSource(new FakeProvider(), new FakeConnectionFactory(db));
        var results = new ConcurrentDictionary<int, PersonRecord>();

        await Parallel.ForEachAsync(Enumerable.Range(1, Invocations),
            new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = TestContext.Current.CancellationToken },
            async (id, ct) =>
            {
                await using var session = dataSource.CreateSession();
                var row = id % 3 == 0
                    ? await FirstOfStream(session, id, ct)
                    : await session.QuerySingleAsync(ById, id, ct);
                Assert.True(results.TryAdd(id, row));
                Assert.Equal(id, await session.ExecuteScalarAsync(EchoScalar, id, ct));
            });

        Assert.Equal(Invocations, results.Count);
        Assert.All(results, kv => Assert.Equal(new PersonRecord(kv.Key, "n" + kv.Key, null), kv.Value));
        Assert.Equal(Invocations * 2, db.Connections.Count);
        Assert.All(db.Commands, c => Assert.Single(c.BoundParameters));
        db.AssertAllReleasedExactlyOnce();
    }

    private static async Task<PersonRecord> FirstOfStream(VelQuerySession session, int id, CancellationToken ct)
    {
        await foreach (var row in session.StreamAsync(ById, id, ct))
        {
            return row;
        }

        throw new InvalidOperationException("no row");
    }
}
