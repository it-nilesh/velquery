using Xunit;

namespace VelQuery.Analyzers.Tests;

/// <summary>VQ1008: SQL built from non-constant values (PRD §7.1, VQ-SEC-001).</summary>
public sealed class DynamicSqlAnalyzerTests
{
    private static string InMethod(string body) => $$"""
        public sealed class Row { public long Id { get; set; } }

        public static class Calls
        {
            public const string Table = "dbo.Users";

            public static async Task Run(IVelQuerySession session, VelQuerySession concrete, string name, int id, string sql)
            {
                {{body}}
            }
        }
        """;

    [Fact]
    public void Interpolation_with_non_constant_hole_is_reported_on_every_session_method()
        => DiagnosticVerifier.Verify(InMethod("""
            await session.QueryAsync<Row>({|VQ1008:$"SELECT Id FROM T WHERE Name = '{name}'"|});
                    await session.QueryFirstAsync<Row>({|VQ1008:$"SELECT Id FROM T WHERE Id = {id}"|});
                    await session.QueryFirstOrDefaultAsync<Row>({|VQ1008:$"SELECT {name}"|});
                    await session.QuerySingleAsync<Row>({|VQ1008:$"SELECT {name}"|});
                    await session.QuerySingleOrDefaultAsync<Row>({|VQ1008:$"SELECT {name}"|});
                    await foreach (var row in session.StreamAsync<Row>({|VQ1008:$"SELECT {name}"|})) { }
                    await session.ExecuteAsync({|VQ1008:$"DELETE FROM {name}"|});
                    await session.ExecuteScalarAsync<long>({|VQ1008:$"SELECT COUNT(*) FROM {name}"|});
                    await using var multi = await session.QueryMultipleAsync({|VQ1008:$"SELECT {name}"|});
                    await session.Sql({|VQ1008:$"SELECT {name}"|}).ExecuteAsync();
                    await concrete.QueryAsync<Row>(sql: {|VQ1008:$"SELECT {name}"|}, cancellationToken: default);
            """));

    [Fact]
    public void Concatenation_with_non_constant_operand_is_reported()
        => DiagnosticVerifier.Verify(InMethod("""
            await session.QueryAsync<Row>({|VQ1008:"SELECT Id FROM " + name|});
                    await session.ExecuteAsync({|VQ1008:"DELETE FROM T WHERE Id = " + id + ";"|});
                    await concrete.Sql(({|VQ1008:"SELECT " + (name)|})).ExecuteAsync();
            """));

    [Fact]
    public void Constant_sql_variables_and_parameters_are_not_reported()
        => DiagnosticVerifier.Verify(InMethod("""
            const string Where = " WHERE Id = @Id";
                    await session.QueryAsync<Row>("SELECT Id FROM " + Table + Where, new QueryParameters().Add("Id", id));
                    await session.QueryAsync<Row>($"SELECT Id FROM {Table}{Where}");
                    await session.QueryAsync<Row>(sql);
                    await session.ExecuteAsync("DELETE FROM T WHERE Name = @Name", new QueryParameters().Add("Name", name));
                    var text = $"SELECT {name}"; // not traced through locals (documented limitation)
                    await session.QueryAsync<Row>(text);
                    _ = string.Concat("a", name);
            """));

    [Fact]
    public void Message_points_to_query_parameters()
    {
        var messages = DiagnosticVerifier.Messages(InMethod("""await session.ExecuteAsync($"DELETE FROM {name}");"""));

        var message = Assert.Single(messages);
        Assert.StartsWith("VQ1008: SQL passed to 'ExecuteAsync' is built by interpolation from non-constant values", message, System.StringComparison.Ordinal);
        Assert.Contains("QueryParameters", message, System.StringComparison.Ordinal);
    }
}
