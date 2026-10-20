using System.Data;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Core.UnitTests.Fakes;
using VelQuery.Providers;
using Xunit;

namespace VelQuery.Core.UnitTests;

/// <summary>Declared provider capabilities are enforced: missing features fail fast with a clear error.</summary>
public sealed class CapabilityEnforcementTests
{
    private const string Sql = "select v from t";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Harness Without(ProviderCapabilities capability)
    {
        var h = new Harness();
        h.Provider.RemovedCapabilities = capability;
        h.Script(Sql, FakeResultSet.Ints("v", 1));
        h.Script("proc", FakeResultSet.Ints("v", 1));
        return h;
    }

    [Fact]
    public async Task Stored_procedures_require_the_capability()
    {
        var h = Without(ProviderCapabilities.StoredProcedures);
        await using var session = h.Session();

        var ex = await Assert.ThrowsAsync<VelQueryUnsupportedCapabilityException>(() => session.Sql("proc").AsStoredProcedure().ExecuteAsync(Ct));

        Assert.Contains("stored procedures", ex.Message, System.StringComparison.Ordinal);
        Assert.Empty(h.Db.Commands);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Output_parameters_require_the_capability()
    {
        var h = Without(ProviderCapabilities.OutputParameters);
        await using var session = h.Session();
        var parameters = new QueryParameters();
        parameters.AddOutput<int>("Out", DbType.Int32);

        await Assert.ThrowsAsync<VelQueryUnsupportedCapabilityException>(() => session.ExecuteAsync(Sql, parameters, Ct));

        Assert.All(h.Db.Commands, c => Assert.Null(c.ExecutedAs));
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Multiple_results_require_the_capability()
    {
        var h = Without(ProviderCapabilities.MultipleResults);
        await using var session = h.Session();

        await Assert.ThrowsAsync<VelQueryUnsupportedCapabilityException>(() => session.QueryMultipleAsync(Sql, null, Ct));

        Assert.Empty(h.Db.Commands);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Input_parameters_and_text_commands_need_no_optional_capability()
    {
        var h = Without(ProviderCapabilities.StoredProcedures | ProviderCapabilities.OutputParameters | ProviderCapabilities.MultipleResults);
        await using var session = h.Session();

        Assert.Equal(1, await session.ExecuteScalarAsync<int>(Sql, new QueryParameters().Add("Id", 1), Ct));
    }
}
