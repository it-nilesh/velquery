using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace VelQuery.MongoDB.IntegrationTests;

/// <summary>Cardinality semantics of generated find, count and aggregate contracts (VQ-FR-013).</summary>
public sealed class CardinalityTests(MongoFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Find_buffers_every_matching_document_in_sort_order()
    {
        await using var session = Session();
        var people = await PeopleByStatus.ExecuteAsync(session, "active", 60, Ct);

        var expected = Enumerable.Range(1, MongoFixture.PeopleCount).Where(i => i % 3 != 0 && 18 + (i % 50) >= 60).ToArray();
        Assert.Equal(expected, people.Select(p => p.Id).ToArray());
        Assert.All(people, p => Assert.Equal("active", p.Status));
        Assert.Equal(new Person(44, "person-44", 18 + 44, "active"), people.Single(p => p.Id == 44));
    }

    [Fact]
    public async Task Skip_and_limit_page_the_sorted_result()
    {
        await using var session = Session();
        var page = await PeoplePage.ExecuteAsync(session, Ct);
        Assert.Equal([11, 12, 13, 14, 15], page.Select(p => p.Id).ToArray());
    }

    [Fact]
    public async Task Single_or_default_returns_the_document_or_null()
    {
        await using var session = Session();
        Assert.Equal("person-7", (await PersonById.ExecuteAsync(session, 7, Ct))!.Name);
        Assert.Null(await PersonById.ExecuteAsync(session, 100_000, Ct));
    }

    [Fact]
    public async Task Single_requires_exactly_one_document()
    {
        await using var session = Session();
        Assert.Equal(7, (await PersonByIdRequired.ExecuteAsync(session, 7, Ct)).Id);

        var none = await Assert.ThrowsAsync<VelQueryNoRowsException>(() => PersonByIdRequired.ExecuteAsync(session, -1, Ct));
        Assert.Equal("people.byId.required", none.QueryId);

        // Many documents match: the second one is detected (limit 2), not silently ignored.
        var many = await Assert.ThrowsAsync<VelQueryTooManyRowsException>(() => SinglePerson.ExecuteAsync(session, "active", Ct));
        Assert.Equal("people.single", many.QueryId);
    }

    [Fact]
    public async Task First_takes_the_first_in_sort_order_and_requires_one()
    {
        await using var session = Session();
        Assert.Equal(100, (await FirstPerson.ExecuteAsync(session, "active", Ct)).Id);
        await Assert.ThrowsAsync<VelQueryNoRowsException>(() => FirstPerson.ExecuteAsync(session, "nobody", Ct));

        Assert.Equal(3, (await FirstPersonOrDefault.ExecuteAsync(session, "inactive", Ct))!.Id);
        Assert.Null(await FirstPersonOrDefault.ExecuteAsync(session, "nobody", Ct));
    }

    [Fact]
    public async Task Count_reports_the_exact_number_of_matches()
    {
        await using var session = Session();
        Assert.Equal(33, await CountByStatus.ExecuteAsync(session, "inactive", Ct));
        Assert.Equal(67, await CountByStatus.ExecuteAsync(session, "active", Ct));
        Assert.Equal(0, await CountByStatus.ExecuteAsync(session, "nobody", Ct));
    }

    [Fact]
    public async Task Aggregate_maps_output_documents_including_the_group_key()
    {
        await using var session = Session();
        var counts = await CountsByStatus.ExecuteAsync(session, Ct);
        Assert.Equal([new StatusCount("active", 67), new StatusCount("inactive", 33)], counts.ToArray());
    }

    [Fact]
    public async Task Aggregate_streams_with_an_integer_limit_argument()
    {
        await using var session = Session();
        var oldest = await OldestPeople.ExecuteAsync(session, "inactive", 3, Ct).ToListAsync(Ct);
        Assert.Equal([99, 48, 96], oldest.Select(p => p.Id).ToArray());
        Assert.Equal([67, 66, 64], oldest.Select(p => p.Age).ToArray());
    }

    [Fact]
    public async Task Aggregate_single_checks_for_a_second_document()
    {
        await using var session = Session();
        await Assert.ThrowsAsync<VelQueryTooManyRowsException>(() => AggregateSingle.ExecuteAsync(session, "active", Ct));
        await Assert.ThrowsAsync<VelQueryNoRowsException>(() => AggregateSingle.ExecuteAsync(session, "nobody", Ct));
    }
}
