using System;
using System.Threading.Tasks;
using Xunit;

namespace VelQuery.SqlServer.IntegrationTests;

/// <summary>VQ-FR-022, VQ-SEC-001, PRD §18.1: bound values can never alter SQL structure.</summary>
public sealed class InjectionTests(SqlServerFixture fixture) : IntegrationTest(fixture)
{
    public static TheoryData<string> Payloads =>
    [
        "' OR 1=1 --",
        "alice' OR '1'='1",
        "'; DROP TABLE dbo.Victims; --",
        "'); DELETE FROM dbo.Victims; --",
        "alice'; UPDATE dbo.Victims SET Name = N'pwned'; --",
        "\"; DROP TABLE dbo.Victims; --",
        "]; DROP TABLE dbo.Victims; --",
        "x' UNION SELECT name, 1 FROM sys.tables --",
        "N'' + CHAR(39) + N'; DROP TABLE dbo.Victims; --",
        "/* */ ' OR 1=1 /*",
        "%' OR Name LIKE N'%",
    ];

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task Payload_in_where_clause_is_data(string payload)
    {
        await using var session = Session();
        var rows = await session.QueryAsync<VictimRow>(
            "SELECT Id, Name FROM dbo.Victims WHERE Name = @Name", new QueryParameters().Add("Name", payload), Ct);
        Assert.Empty(rows);

        var like = await session.QueryAsync<VictimRow>(
            "SELECT Id, Name FROM dbo.Victims WHERE Name LIKE @Pattern", new QueryParameters().Add("Pattern", payload), Ct);
        Assert.Empty(like);

        await AssertVictimsIntactAsync(session);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task Payload_written_by_insert_round_trips_verbatim(string payload)
    {
        var marker = "inj-" + Guid.NewGuid().ToString("N");
        await using var session = Session();
        Assert.Equal(1, await session.ExecuteAsync(
            "INSERT dbo.Scratch (Marker, Value) VALUES (@Marker, @Value)",
            new QueryParameters().Add("Marker", marker).Add("Value", payload), Ct));

        var stored = await session.ExecuteScalarAsync<string>(
            "SELECT Value FROM dbo.Scratch WHERE Marker = @Marker", new QueryParameters().Add("Marker", marker), Ct);
        Assert.Equal(payload, stored);
        await AssertVictimsIntactAsync(session);
    }

    [Fact]
    public async Task Payload_as_stored_procedure_argument_is_data()
    {
        await using var session = Session();
        var p = new QueryParameters().Add("Value", 0);
        var output = p.AddOutput<int>("Result");

        // The procedure signature is int; a string payload must fail conversion server-side, not execute.
        var bad = new QueryParameters().Add("Value", "1; DROP TABLE dbo.Victims; --").AddOutputChain(out _);
        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(
            () => session.Sql("dbo.ProcNoRows", bad).AsStoredProcedure().ExecuteAsync(Ct));
        Assert.IsType<Microsoft.Data.SqlClient.SqlException>(ex.InnerException);

        await session.Sql("dbo.ProcNoRows", p).AsStoredProcedure().ExecuteAsync(Ct);
        Assert.Equal(1, output.Value);
        await AssertVictimsIntactAsync(session);
    }

    [Theory]
    [InlineData("Victims]; DROP TABLE dbo.Victims; --")]
    [InlineData("Victims] WHERE 1=1; DELETE dbo.Victims; --")]
    public async Task Quoted_identifier_payload_cannot_escape_brackets(string identifier)
    {
        await using var session = Session();
        var quoted = session.Provider.QuoteIdentifier(identifier);

        // The whole payload is one (non-existent) object name: SQL Server reports error 208, nothing else runs.
        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(
            () => session.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM dbo.{quoted}", cancellationToken: Ct));
        Assert.Equal(208, Assert.IsType<Microsoft.Data.SqlClient.SqlException>(ex.InnerException).Number);
        await AssertVictimsIntactAsync(session);
    }

    private static async Task AssertVictimsIntactAsync(VelQuerySession session)
    {
        Assert.Equal(3, await session.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Victims", cancellationToken: Ct));
        Assert.Equal(0, await session.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Victims WHERE Name NOT IN (N'alice', N'bob', N'carol')", cancellationToken: Ct));
    }

    public sealed record VictimRow(int Id, string Name);
}

internal static class QueryParametersTestExtensions
{
    public static QueryParameters AddOutputChain(this QueryParameters parameters, out OutputParameter<int> output)
    {
        output = parameters.AddOutput<int>("Result");
        return parameters;
    }
}
