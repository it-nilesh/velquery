using System;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Core.UnitTests.Fakes;
using VelQuery.Mapping;
using VelQuery.Providers;
using Xunit;

namespace VelQuery.Core.UnitTests;

/// <summary>ProviderCapabilities.NativeConversions and the generic ADO.NET provider.</summary>
public sealed class NativeConversionAndGenericProviderTests
{
    private const string Sql = "select v from t";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Harness WithText(string value, bool native)
    {
        var h = new Harness();
        h.Provider.ExtraCapabilities = native ? ProviderCapabilities.NativeConversions : ProviderCapabilities.None;
        h.Script(Sql, new FakeResultSet(("v", typeof(string))).Row(value));
        return h;
    }

    [Fact]
    public async Task Without_native_conversions_text_is_never_parsed()
    {
        var h = WithText("2026-10-20", native: false);
        await using var session = h.Session();

        await Assert.ThrowsAsync<VelQueryMappingException>(() => session.QueryAsync<DateTime>(Sql, null, Ct));
    }

    [Fact]
    public async Task Native_conversions_use_the_driver_typed_getter_for_rows_and_scalars()
    {
        var h = WithText("12.50", native: true);
        await using var session = h.Session();
        var before = Volatile.Read(ref FakeDbDataReader.NativeConversionCalls);

        Assert.Equal(12.50m, Assert.Single(await session.QueryAsync<decimal>(Sql, null, Ct)));
        Assert.Equal(12.50m, await session.ExecuteScalarAsync<decimal>(Sql, null, Ct));
        Assert.True(Volatile.Read(ref FakeDbDataReader.NativeConversionCalls) >= before + 2);
    }

    [Fact]
    public async Task Native_scalar_keeps_no_rows_and_null_semantics()
    {
        var h = new Harness();
        h.Provider.ExtraCapabilities = ProviderCapabilities.NativeConversions;
        h.Script("none", new FakeResultSet(("v", typeof(string))));
        h.Script("null", new FakeResultSet(("v", typeof(string))).Row(DBNull.Value));
        await using var session = h.Session();

        await Assert.ThrowsAsync<VelQueryNoRowsException>(() => session.ExecuteScalarAsync<int>("none", null, Ct));
        Assert.Null(await session.ExecuteScalarAsync<int?>("none", null, Ct));
        await Assert.ThrowsAsync<VelQueryMappingException>(() => session.ExecuteScalarAsync<int>("null", null, Ct));
        Assert.Null(await session.ExecuteScalarAsync<string>("null", null, Ct));
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public void Mapping_context_reflects_provider_capability()
    {
        Assert.False(new MappingContext("q", true).NativeConversions);
        Assert.True(new MappingContext("q", true, NativeConversions: true).NativeConversions);
    }

    [Theory]
    [InlineData("@", "\"", null, "Id", "@Id", "a\"b", "\"a\"\"b\"")]
    [InlineData(":", "\"", null, "Id", ":Id", "x", "\"x\"")]
    [InlineData("", "`", null, "Id", "Id", "a`b", "`a``b`")]
    [InlineData("@", "[", "]", "Id", "@Id", "a]b", "[a]]b]")]
    public void Generic_provider_applies_configured_dialect(string prefix, string open, string? close, string name, string expectedName, string identifier, string expectedQuoted)
    {
        var provider = new GenericAdoNetProvider(new GenericAdoNetProviderOptions { ParameterPrefix = prefix, IdentifierQuote = open, IdentifierQuoteEnd = close });

        Assert.Equal(expectedName, provider.FormatParameterName(name));
        Assert.Equal(expectedQuoted, provider.QuoteIdentifier(identifier));
        Assert.Equal("generic", provider.DialectId);
    }

    [Fact]
    public void Generic_provider_classifies_only_timeout_exceptions()
    {
        var provider = new GenericAdoNetProvider();

        Assert.Equal(ProviderErrorKind.Timeout, provider.Classify(new TimeoutException()));
        Assert.Equal(ProviderErrorKind.Timeout, provider.Classify(new FakeDbException("wrapped", new TimeoutException())));
        Assert.Equal(ProviderErrorKind.Other, provider.Classify(new FakeDbException("other")));
        Assert.Throws<ArgumentException>(() => new GenericAdoNetProvider(new GenericAdoNetProviderOptions { IdentifierQuote = "<<" }));
        Assert.Throws<ArgumentException>(() => provider.QuoteIdentifier(new string('x', 129)));
    }
}
