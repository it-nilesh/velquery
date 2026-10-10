using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Xunit;

namespace VelQuery.SqlServer.IntegrationTests;

/// <summary>VQ-FR-023, PRD §9.4: stored procedures with input, output, input/output and return values.</summary>
public sealed class StoredProcedureTests(SqlServerFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Multi_result_procedure_outputs_are_available_after_all_results_are_consumed()
    {
        await using var session = Session();
        var p = new QueryParameters().Add("Input", 3);
        var doubled = p.AddOutput<int>("Doubled");
        var counter = p.AddOutput<long>("Counter", direction: ParameterDirection.InputOutput, inputValue: 41L);
        var label = p.AddOutput<string>("Label", size: 50);
        var returned = p.AddOutput<int>("ReturnValue", direction: ParameterDirection.ReturnValue);

        await using (var reader = await session.Sql("dbo.ProcAll", p).AsStoredProcedure().QueryMultipleAsync(Ct))
        {
            var rows = await reader.ReadAsync(Mappers.IdName, Ct);
            Assert.Equal([1L, 2L, 3L], rows.Select(r => r.Id));

            // Outputs follow the result sets in the TDS stream; they are not available yet.
            Assert.False(doubled.HasValue);
            Assert.Throws<InvalidOperationException>(() => doubled.Value);

            Assert.Equal(100, await reader.ReadSingleAsync<int>(Ct));
            Assert.False(reader.HasMoreResults);
        }

        Assert.Equal(6, doubled.Value);
        Assert.Equal(42L, counter.Value);
        Assert.Equal("input=3", label.Value);
        Assert.Equal(42, returned.Value);
    }

    [Fact]
    public async Task Buffered_query_on_procedure_drains_and_captures_outputs()
    {
        await using var session = Session();
        var p = new QueryParameters().Add("Input", 5);
        var doubled = p.AddOutput<int>("Doubled");
        var counter = p.AddOutput<long>("Counter", direction: ParameterDirection.InputOutput, inputValue: 0L);
        var label = p.AddOutput<string>("Label", size: 50);
        var returned = p.AddOutput<int>("Ret", direction: ParameterDirection.ReturnValue);

        var rows = await session.Sql("dbo.ProcAll", p).AsStoredProcedure().QueryAsync<IdName>(Ct);

        Assert.Equal(5, rows.Count);
        Assert.Equal(10, doubled.Value);
        Assert.Equal(1L, counter.Value);
        Assert.Equal("input=5", label.Value);
        Assert.Equal(42, returned.Value);
    }

    [Fact]
    public async Task Streamed_procedure_captures_outputs_after_enumeration_completes()
    {
        await using var session = Session();
        var p = new QueryParameters().Add("Input", 2);
        var doubled = p.AddOutput<int>("Doubled");
        p.AddOutput<long>("Counter", direction: ParameterDirection.InputOutput, inputValue: 0L);
        p.AddOutput<string>("Label", size: 50);
        var returned = p.AddOutput<int>("Ret", direction: ParameterDirection.ReturnValue);

        var count = 0;
        await foreach (var _ in session.Sql("dbo.ProcAll", p).AsStoredProcedure().StreamAsync<IdName>(Ct))
        {
            count++;
            Assert.False(doubled.HasValue);
        }

        Assert.Equal(2, count);
        Assert.Equal(4, doubled.Value);
        Assert.Equal(42, returned.Value);
    }

    [Fact]
    public async Task String_output_without_declared_size_uses_stable_bucket()
    {
        await using var session = Session();
        var p = new QueryParameters().Add("Input", 9);
        p.AddOutput<int>("Doubled");
        p.AddOutput<long>("Counter", direction: ParameterDirection.InputOutput, inputValue: 0L);
        var label = p.AddOutput<string>("Label");

        await session.Sql("dbo.ProcAll", p).AsStoredProcedure().ExecuteAsync(Ct);
        Assert.Equal("input=9", label.Value);
    }

    [Fact]
    public async Task First_on_procedure_still_captures_outputs()
    {
        await using var session = Session();
        var p = new QueryParameters().Add("Input", 4);
        var doubled = p.AddOutput<int>("Doubled");
        p.AddOutput<long>("Counter", direction: ParameterDirection.InputOutput, inputValue: 0L);
        p.AddOutput<string>("Label", size: 50);
        var returned = p.AddOutput<int>("Ret", direction: ParameterDirection.ReturnValue);

        var first = await session.Sql("dbo.ProcAll", p).AsStoredProcedure().QueryFirstAsync<IdName>(Ct);
        Assert.Equal(1, first.Id);
        Assert.Equal(8, doubled.Value);
        Assert.Equal(42, returned.Value);
    }

    [Fact]
    public async Task Execute_on_procedure_captures_output_and_return_value()
    {
        await using var session = Session();
        var p = new QueryParameters().Add("Value", 7);
        var result = p.AddOutput<int>("Result");
        var returned = p.AddOutput<int>("Ret", direction: ParameterDirection.ReturnValue);

        // The procedure runs no DML, so SQL Server reports no count.
        Assert.Equal(-1, await session.Sql("dbo.ProcNoRows", p).AsStoredProcedure().ExecuteAsync(Ct));
        Assert.Equal(8, result.Value);
        Assert.Equal(70, returned.Value);
    }

    [Fact]
    public async Task Null_output_into_nullable_handle()
    {
        await using var session = Session();
        var p = new QueryParameters().Add<int?>("Value", null);
        var result = p.AddOutput<int?>("Result");
        var returned = p.AddOutput<int>("Ret", direction: ParameterDirection.ReturnValue);

        await session.Sql("dbo.ProcNoRows", p).AsStoredProcedure().ExecuteAsync(Ct);
        Assert.True(result.HasValue);
        Assert.Null(result.Value);

        // RETURN NULL * 10 => SQL Server returns 0 and a warning; never NULL.
        Assert.Equal(0, returned.Value);
    }

    [Fact]
    public async Task Procedure_error_in_later_result_surfaces_from_buffered_query()
    {
        await using var session = Session();
        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(
            () => session.Sql("dbo.ProcFails", new QueryParameters().Add("Value", 1)).AsStoredProcedure().QueryAsync<int>(Ct));
        Assert.Equal(8134, Assert.IsType<SqlException>(ex.InnerException).Number);
    }
}
