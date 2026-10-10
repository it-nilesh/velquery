using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using VelQuery.Parameters;
using Xunit;

namespace VelQuery.SqlServer.IntegrationTests;

/// <summary>PRD §9.4 / VQ-FR-008: stable SQL Server parameter metadata so plans are reused, and native types honored.</summary>
public sealed class ParameterMetadataTests(SqlServerFixture fixture) : IntegrationTest(fixture)
{
    [Theory]
    [InlineData(3, SqlDbType.NVarChar, 4000)]
    [InlineData(30, SqlDbType.NVarChar, 4000)]
    [InlineData(4000, SqlDbType.NVarChar, 4000)]
    [InlineData(4001, SqlDbType.NVarChar, -1)]
    public void Unicode_string_size_is_bucketed(int length, SqlDbType expectedType, int expectedSize)
    {
        var parameter = Bind(new ParameterSpec("P", DbType.String), new string('x', length));
        Assert.Equal(expectedType, parameter.SqlDbType);
        Assert.Equal(expectedSize, parameter.Size);
    }

    [Theory]
    [InlineData(3, 8000)]
    [InlineData(8000, 8000)]
    [InlineData(8001, -1)]
    public void Ansi_string_and_binary_sizes_are_bucketed(int length, int expectedSize)
    {
        var ansi = Bind(new ParameterSpec("P", DbType.AnsiString), new string('x', length));
        Assert.Equal(SqlDbType.VarChar, ansi.SqlDbType);
        Assert.Equal(expectedSize, ansi.Size);

        var binary = Bind(new ParameterSpec("P", DbType.Binary), new byte[length]);
        Assert.Equal(SqlDbType.VarBinary, binary.SqlDbType);
        Assert.Equal(expectedSize, binary.Size);
    }

    [Fact]
    public void Explicit_size_and_native_type_are_honored()
    {
        var sized = Bind(new ParameterSpec("P", DbType.String, Size: 50), "abc");
        Assert.Equal(50, sized.Size);

        var varchar = Bind(new ParameterSpec("P", DbType.AnsiString, Size: 20, ProviderDbType: (int)SqlDbType.VarChar), "abc");
        Assert.Equal(SqlDbType.VarChar, varchar.SqlDbType);
        Assert.Equal(20, varchar.Size);

        var nativeOnly = Bind(new ParameterSpec("P", ProviderDbType: (int)SqlDbType.VarChar), "abc");
        Assert.Equal(SqlDbType.VarChar, nativeOnly.SqlDbType);
        Assert.True(nativeOnly.Size is 4000 or 8000, $"size {nativeOnly.Size}");

        var nullString = Bind(new ParameterSpec("P", DbType.String), null);
        Assert.Equal(SqlDbType.NVarChar, nullString.SqlDbType);
        Assert.Equal(4000, nullString.Size);
        Assert.Equal(DBNull.Value, nullString.Value);
    }

    [Fact]
    public async Task Strings_of_different_lengths_share_one_cached_plan()
    {
        await using var session = Session();
        var marker = Guid.NewGuid().ToString("N");
        var sql = $"SELECT COUNT(*) FROM dbo.Rows100 WHERE Name = @Name /* plan-{marker} */";

        foreach (var value in new[] { "abc", new string('y', 30), "row-7", new string('z', 3999) })
        {
            await session.ExecuteScalarAsync<int>(sql, new QueryParameters().Add("Name", value), Ct);
        }

        var plans = await CachedPlanTextsAsync(session, marker);
        var plan = Assert.Single(plans);
        Assert.Contains("@Name nvarchar(4000)", plan, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Long_strings_use_max_bucket_with_one_plan()
    {
        await using var session = Session();
        var marker = Guid.NewGuid().ToString("N");
        var sql = $"SELECT LEN(@Name) /* plan-{marker} */";

        foreach (var length in new[] { 4001, 5000, 12000 })
        {
            Assert.Equal(length, await session.ExecuteScalarAsync<int>(sql, new QueryParameters().Add("Name", new string('q', length)), Ct));
        }

        var plan = Assert.Single(await CachedPlanTextsAsync(session, marker));
        Assert.Contains("@Name nvarchar(max)", plan, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explicit_varchar_provider_type_reaches_the_server()
    {
        await using var session = Session();
        var marker = Guid.NewGuid().ToString("N");
        var sql = $"SELECT CAST(SQL_VARIANT_PROPERTY(CAST(@Code AS sql_variant), 'BaseType') AS nvarchar(20)) /* plan-{marker} */";
        var p = new QueryParameters().Add(new ParameterSpec("Code", DbType.AnsiString, Size: 20, ProviderDbType: (int)SqlDbType.VarChar), "abc");

        Assert.Equal("varchar", await session.ExecuteScalarAsync<string>(sql, p, Ct));
        var plan = Assert.Single(await CachedPlanTextsAsync(session, marker));
        Assert.Contains("@Code varchar(20)", plan, StringComparison.OrdinalIgnoreCase);

        // Without the explicit type a CLR string is Unicode.
        Assert.Equal("nvarchar", await session.ExecuteScalarAsync<string>(sql, new QueryParameters().Add("Code", "abc"), Ct));
    }

    [Fact]
    public async Task Varchar_comparison_against_varchar_column_uses_ansi_parameter()
    {
        // An nvarchar parameter against a varchar column forces an implicit conversion of the column; an ANSI
        // parameter does not. Both must still return the right row.
        await using var session = Session();
        var ansi = new QueryParameters().Add("Text", "ansi", DbType.AnsiString);
        Assert.Equal(1, await session.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Types WHERE AnsiText = @Text", ansi, Ct));
        Assert.Equal(1, await session.ExecuteScalarAsync<int>(
            "SELECT Id FROM dbo.Types WHERE AnsiText = @Text", new QueryParameters().Add("Text", "ansi"), Ct));
    }

    private static SqlParameter Bind(in ParameterSpec spec, object? value)
    {
        using var command = new SqlCommand();
        return (SqlParameter)ParameterWriter.Add(command, SqlServerProvider.Instance, spec, value);
    }

    private static async Task<IReadOnlyList<string>> CachedPlanTextsAsync(VelQuerySession session, string marker)
        => await session.QueryAsync<string>("""
            SELECT st.text
            FROM sys.dm_exec_cached_plans cp
            CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) st
            WHERE st.text LIKE @Pattern AND st.text NOT LIKE N'%dm_exec_cached_plans%'
            """, new QueryParameters().Add("Pattern", "%plan-" + marker + "%"), Ct);
}
