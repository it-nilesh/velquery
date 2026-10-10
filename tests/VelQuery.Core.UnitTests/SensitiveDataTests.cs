using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Core.UnitTests.Fakes;
using VelQuery.Parameters;
using Xunit;

namespace VelQuery.Core.UnitTests;

/// <summary>
/// VQ-FR-017, VQ-FR-025, VQ-SEC-003: exceptions carry query IDs, columns and types but never parameter values, row
/// values or connection strings. Canary strings are bound as parameters and stored in cells, then searched for in the
/// full <see cref="Exception.ToString"/> (scripted provider exceptions never contain the canary themselves).
/// </summary>
public sealed class SensitiveDataTests
{
    private const string Canary = "CANARY-SECRET-123";
    private const string Sql = "select * from people where Name = @Name";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static QueryParameters CanaryParameters() => new QueryParameters().Add("Name", Canary).Add("Other", Canary, DbType.AnsiString, 40);

    private static FakeResultSet CanaryRows(int count, object? id = null)
    {
        var set = new FakeResultSet(("Id", typeof(int)), ("Name", typeof(string)), ("Email", typeof(string)));
        for (var i = 0; i < count; i++)
        {
            set.Row(id ?? i, Canary, Canary);
        }

        return set;
    }

    private static void AssertClean(Exception ex)
    {
        var text = ex.ToString();
        Assert.DoesNotContain(Canary, text, StringComparison.Ordinal);
        Assert.DoesNotContain("CONNSTR-SECRET", text, StringComparison.Ordinal);
    }

    private static async Task<Exception> Capture(Func<VelQuerySession, Task> run, FakeScript script, bool fallback = true)
    {
        var h = fallback ? Harness.WithFallback() : new Harness();
        h.Db.Script(Sql, script);
        await using var s = h.Session();
        var ex = await Record.ExceptionAsync(() => run(s));
        Assert.NotNull(ex);
        Assert.IsAssignableFrom<VelQueryException>(ex);

        // The canary really was bound/returned, so absence from the message is meaningful.
        Assert.Contains(h.Db.Commands, c => c.BoundParameters.Count == 0 || c.BoundParameters[0].Value as string == Canary);
        return ex;
    }

    [Fact]
    public async Task Provider_error_does_not_leak_parameter_values()
        => AssertClean(await Capture(s => s.QueryAsync<PersonClass>(Sql, CanaryParameters(), Ct),
            new FakeScript { ExecuteFault = new FakeDbException() }));

    [Fact]
    public async Task Timeout_does_not_leak_parameter_values()
        => AssertClean(await Capture(s => s.ExecuteAsync(Sql, CanaryParameters(), Ct),
            new FakeScript { ExecuteFault = new FakeDbException("timeout", isTimeout: true) }));

    [Fact]
    public async Task Strict_null_mapping_does_not_leak_row_values()
    {
        var ex = await Capture(s => s.QueryAsync<PersonClass>(Sql, CanaryParameters(), Ct), new FakeScript().With(CanaryRows(1, DBNull.Value)));
        Assert.IsType<VelQueryMappingException>(ex);
        AssertClean(ex);
    }

    [Fact]
    public async Task Unconvertible_cell_does_not_leak_the_cell_value()
    {
        var set = new FakeResultSet(("Id", typeof(int)), ("Count", typeof(string))).Row(1, Canary);
        var ex = await Capture(s => s.QueryAsync<LooseRow>(Sql, CanaryParameters(), Ct), new FakeScript().With(set));
        Assert.IsType<VelQueryMappingException>(ex);
        AssertClean(ex);
    }

    [Fact]
    public async Task Unconvertible_scalar_does_not_leak_the_value()
    {
        var set = FakeResultSet.Column<string>("c", Canary);
        var ex = await Capture(s => s.ExecuteScalarAsync<int>(Sql, CanaryParameters(), Ct), new FakeScript().With(set));
        Assert.IsType<VelQueryMappingException>(ex);
        AssertClean(ex);
    }

    [Fact]
    public async Task Cardinality_errors_do_not_leak_values()
    {
        AssertClean(await Capture(s => s.QuerySingleAsync<PersonClass>(Sql, CanaryParameters(), Ct), new FakeScript().With(CanaryRows(2))));
        AssertClean(await Capture(s => s.QueryFirstAsync<PersonClass>(Sql, CanaryParameters(), Ct), new FakeScript().With(CanaryRows(0))));
    }

    [Fact]
    public async Task Output_parameter_conversion_failure_does_not_leak_the_output_value()
    {
        var script = new FakeScript { RecordsAffected = 1 };
        script.OutputValues["Total"] = Canary;
        var ex = await Capture(s =>
        {
            var parameters = CanaryParameters();
            parameters.AddOutput<int>("Total");
            return s.ExecuteAsync(Sql, parameters, Ct);
        }, script);
        Assert.IsType<VelQueryMappingException>(ex);
        AssertClean(ex);
    }

    [Fact]
    public async Task Descriptor_binder_values_do_not_leak_on_provider_error()
    {
        var descriptor = new QueryDescriptor<string, int>("people.by-name", Sql, Mapping.ScalarRowMapperFactory<int>.Instance,
            static (command, provider, name) => ParameterWriter.Add(command, provider, new ParameterSpec("Name", DbType.String, 100), name));
        var ex = await Capture(s => s.QueryAsync(descriptor, Canary, Ct), new FakeScript { ExecuteFault = new FakeDbException() }, fallback: false);
        Assert.Equal("people.by-name", ((VelQueryException)ex).QueryId);
        AssertClean(ex);
    }

    [Fact]
    public async Task Bound_values_never_alter_sql_text()
    {
        const string injection = "' OR 1=1 --";
        var h = new Harness();
        h.Db.Script(Sql, new FakeScript { RecordsAffected = 0 });
        await using var s = h.Session();

        await s.ExecuteAsync(Sql, new QueryParameters().Add("Name", injection), Ct);
        await s.ExecuteAsync(Sql, QueryParameters.FromObject(new { Name = injection }), Ct);

        Assert.All(h.Db.Commands, c =>
        {
            Assert.Equal(Sql, c.CommandText);
            var p = Assert.Single(c.BoundParameters);
            Assert.Equal("@Name", p.Name);
            Assert.Equal(injection, p.Value);
        });
    }

    [Fact]
    public async Task Runtime_sql_identity_never_contains_values_or_sql_text()
    {
        var h = new Harness();
        h.Db.Script(Sql, new FakeScript { ExecuteFault = new FakeDbException() });
        await using var s = h.Session();

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => s.ExecuteAsync(Sql, CanaryParameters(), Ct));

        Assert.StartsWith("sql:", ex.QueryId, StringComparison.Ordinal);
        Assert.DoesNotContain("people", ex.Message, StringComparison.Ordinal);
        AssertClean(ex);
    }
}
