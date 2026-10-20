using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace VelQuery.MongoDB.IntegrationTests;

/// <summary>
/// NoSQL-injection guarantee (ADR-0017): arguments are always bound as values. Operator-shaped strings stay strings,
/// and inside aggregation expressions a string such as "$Secret" stays a string instead of becoming a field path.
/// Compile-time rejection of placeholders in key/operator/code positions is covered by the generator tests.
/// </summary>
public sealed class InjectionTests(MongoFixture fixture) : IntegrationTest(fixture)
{
    public static TheoryData<string> OperatorShapedStrings => new(
        """{ "$ne": null }""",
        """{"$gt": ""}""",
        "$ne",
        "$where",
        "this.Name.length > 0",
        """'; return true; var x='""",
        """{ "$regex": ".*" }""",
        "$$ROOT",
        "\0$ne");

    [Theory]
    [MemberData(nameof(OperatorShapedStrings))]
    public async Task Operator_shaped_strings_are_compared_literally(string hostile)
    {
        await using var session = Session();
        Assert.Empty(await PeopleByName.ExecuteAsync(session, hostile, Ct));
        Assert.Empty(await PeopleByNameExpression.ExecuteAsync(session, hostile, Ct));
        Assert.Equal(new[] { "person-1" }, (await PeopleByNames.ExecuteAsync(session, ["person-1", hostile], Ct)).Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task A_document_with_an_operator_shaped_string_value_is_matched_exactly()
    {
        // Store a person whose name *is* an operator-shaped string: it must match itself and nothing else.
        var people = Fixture.Raw("people");
        await people.InsertOneAsync(new BsonDocument { { "_id", 9_001 }, { "Name", """{ "$ne": null }""" }, { "Age", 1 }, { "Status", "hostile" } }, cancellationToken: Ct);
        try
        {
            await using var session = Session();
            var found = await PeopleByName.ExecuteAsync(session, """{ "$ne": null }""", Ct);
            Assert.Equal(new[] { 9_001 }, found.Select(p => p.Id).ToArray());
        }
        finally
        {
            await people.DeleteOneAsync(new BsonDocument("_id", 9_001), Ct);
        }
    }

    [Fact]
    public async Task Field_path_strings_inside_expressions_stay_literal()
    {
        await using var session = Session();

        // Without $literal wrapping, { $eq: [ "$Name", "$Name" ] } would be true for every document.
        Assert.Empty(await PeopleByNameExpression.ExecuteAsync(session, "$Name", Ct));
        Assert.Equal(new[] { 5 }, (await PeopleByNameExpression.ExecuteAsync(session, "person-5", Ct)).Select(p => p.Id).ToArray());

        // Without $literal wrapping, "$Secret" would project the secret field's value.
        var labeled = await LabeledPerson.ExecuteAsync(session, 1, "$Secret", Ct);
        Assert.Equal("$Secret", labeled!.Label);
        Assert.Equal("$$ROOT", (await LabeledPerson.ExecuteAsync(session, 1, "$$ROOT", Ct))!.Label);
    }

    [Fact]
    public async Task Null_argument_is_bson_null_not_a_missing_condition()
    {
        await using var session = Session();

        // { Name: null } matches documents whose Name is null or missing: none of the seeded people.
        Assert.Empty(await PeopleByName.ExecuteAsync(session, null, Ct));
    }

    [Fact]
    public async Task Regex_arguments_are_patterns_by_design_but_cannot_add_operators()
    {
        await using var session = Session();
        Assert.Equal(new[] { 1 }, (await PeopleByNameRegex.ExecuteAsync(session, "^person-1$", Ct)).Select(p => p.Id).ToArray());
        Assert.Empty(await PeopleByNameRegex.ExecuteAsync(session, """{ "$ne": null }""", Ct));
    }
}
