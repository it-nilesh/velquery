using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VelQuery.Cli.Schema;
using VelQuery.Testing;
using Xunit;

namespace VelQuery.Cli.Tests;

/// <summary>Snapshot format, drift classification and command behaviour (no Docker needed).</summary>
public sealed class CliTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("velquery-cli-").FullName;

    private static DbSchema Sample(params DbObject[] extra) => new("sqlserver", "shop", "dbo", false, false,
    [
        new DbObject("dbo", "Users", "table",
        [
            new DbColumn("Id", "bigint", null, null, null, false, true, false),
            new DbColumn("Name", "nvarchar", 100, null, null, false, false, false),
            new DbColumn("Email", "nvarchar", 320, null, null, true, false, false),
        ]),
        new DbObject("dbo", "Orders", "table",
        [
            new DbColumn("Id", "int", null, null, null, false, true, false),
            new DbColumn("Total", "decimal", null, 10, 2, false, false, false),
        ]),
        .. extra,
    ]);

    [Fact]
    public void Canonical_json_is_deterministic_sorted_and_hashed()
    {
        var a = Sample().ToCanonicalJson();
        var b = (Sample() with { Objects = Sample().Objects.Reverse().ToList() }).ToCanonicalJson();
        Assert.Equal(a, b);
        Assert.DoesNotContain("\r", a, StringComparison.Ordinal);
        Assert.True(a.IndexOf("\"Orders\"", StringComparison.Ordinal) < a.IndexOf("\"Users\"", StringComparison.Ordinal));
        Assert.Contains("{ \"name\": \"Total\", \"dbType\": \"decimal\", \"precision\": 10, \"scale\": 2, \"nullable\": false }", a, StringComparison.Ordinal);

        // snapshotSha256 is the hash of the canonical text without its own line.
        var hashLine = a.Split('\n').Single(l => l.Contains("snapshotSha256", StringComparison.Ordinal));
        var withoutHash = a.Replace(hashLine + "\n", string.Empty, StringComparison.Ordinal);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(withoutHash)));
        Assert.Contains(expected, hashLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_round_trips_the_canonical_form()
    {
        var json = Sample().ToCanonicalJson();
        Assert.Equal(json, DbSchema.Parse(json).ToCanonicalJson());
    }

    [Fact]
    public void Generator_reads_what_the_cli_writes()
    {
        // The CLI's writer and the generator's reader are separate implementations of one format; prove they agree.
        var source = """
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using VelQuery;
            public sealed record UserRow(long Id, string Name, string? Email);
            [VelQuerySql("users.all", "SELECT Id, Name, Emial FROM dbo.Users")]
            public static partial class AllUsers { public static partial Task<IReadOnlyList<UserRow>> ExecuteAsync(IVelQuerySession session); }
            """;
        var diagnostics = GeneratorHarness.GetVelQueryDiagnostics(GeneratorHarness.Compile(source), null, [("/shop.vqschema.json", Sample().ToCanonicalJson())])
            .Select(d => d.Id + ": " + d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
        [
            "VQ1005: Query 'users.all': column 'Emial' in 'dbo.Users' does not exist in the schema snapshot; did you mean 'Email'?",
        ], diagnostics);
    }

    [Fact]
    public void Diff_classifies_breaking_and_safe_changes()
    {
        var approved = Sample();
        var current = new DbSchema("sqlserver", "shop", "dbo", false, false,
        [
            new DbObject("dbo", "Users", "table",
            [
                new DbColumn("Id", "bigint", null, null, null, false, true, false),
                new DbColumn("Name", "nvarchar", 50, null, null, true, false, false),   // shrank and became nullable
                new DbColumn("Phone", "nvarchar", 20, null, null, false, false, false), // required for inserts
                new DbColumn("Note", "nvarchar", 20, null, null, true, false, false),   // optional
            ]),
            new DbObject("dbo", "Audit", "table", [new DbColumn("Id", "int", null, null, null, false, true, false)]),
        ]);

        var changes = SchemaDiff.Compare(approved, current).Select(c => $"{(c.Breaking ? "BREAKING" : "ok")} {c.Kind} {c.Target}").Order(StringComparer.Ordinal);
        Assert.Equal(
        [
            "BREAKING became nullable dbo.Users.Name",
            "BREAKING column added dbo.Users.Phone",
            "BREAKING column removed dbo.Users.Email",
            "BREAKING size changed dbo.Users.Name",
            "BREAKING table removed dbo.Orders",
            "ok column added dbo.Users.Note",
            "ok table added dbo.Audit",
        ], changes);
        Assert.Empty(SchemaDiff.Compare(approved, Sample()));
    }

    [Fact]
    public void Diff_treats_unbounded_to_bounded_as_breaking_and_the_reverse_as_safe()
    {
        DbSchema With(int? length, int? precision) => new("sqlserver", null, "dbo", false, false,
            [new DbObject("dbo", "T", "table", [new DbColumn("Note", "nvarchar", length, precision, null, true, false, false)])]);

        var shrink = Assert.Single(SchemaDiff.Compare(With(null, null), With(50, null)));
        Assert.True(shrink.Breaking);
        Assert.Equal("unbounded -> 50", shrink.Detail);
        Assert.False(Assert.Single(SchemaDiff.Compare(With(50, null), With(null, null))).Breaking);
        Assert.True(Assert.Single(SchemaDiff.Compare(With(null, null), With(null, 10))).Breaking);
        Assert.False(Assert.Single(SchemaDiff.Compare(With(50, null), With(100, null))).Breaking);
    }

    [Fact]
    public async Task Snapshot_check_and_diff_commands_against_sqlite()
    {
        var db = Path.Combine(_directory, "shop.db");
        var connection = new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ConnectionString;
        await ExecuteAsync(connection, "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT)");
        var env = new Dictionary<string, string?> { ["SHOP_DB"] = connection };
        var output = Path.Combine(_directory, "schema", "shop.vqschema.json");

        var (code, stdout, _) = await RunAsync(env, "schema", "snapshot", "--provider", "sqlite", "--connection-env", "SHOP_DB", "--output", output);
        Assert.Equal(CliApp.Ok, code);
        Assert.Contains("1 objects, 3 columns (sqlite)", stdout, StringComparison.Ordinal);
        var first = await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken);

        (code, stdout, _) = await RunAsync(env, "schema", "snapshot", "--provider", "sqlite", "--connection-env", "SHOP_DB", "--output", output, "--check");
        Assert.Equal(CliApp.Ok, code);
        Assert.Contains("is up to date", stdout, StringComparison.Ordinal);

        // A breaking change: drop a column the snapshot (and so the checked queries) relied on.
        await ExecuteAsync(connection, "ALTER TABLE users DROP COLUMN email; ALTER TABLE users ADD COLUMN phone TEXT");
        (code, stdout, _) = await RunAsync(env, "schema", "snapshot", "--provider", "sqlite", "--connection-env", "SHOP_DB", "--output", output, "--check");
        Assert.Equal(CliApp.Differences, code);
        Assert.Contains("BREAKING  column removed", stdout, StringComparison.Ordinal);
        Assert.Equal(first, await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken)); // --check never writes

        (code, stdout, _) = await RunAsync(env, "schema", "diff", "--snapshot", output, "--provider", "sqlite", "--connection-env", "SHOP_DB");
        Assert.Equal(CliApp.Differences, code);
        Assert.Contains("2 change(s), 1 breaking", stdout, StringComparison.Ordinal);
        Assert.Contains("ok        column added         users.phone", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_without_breaking_changes_passes_unless_fail_on_any()
    {
        var db = Path.Combine(_directory, "safe.db");
        var connection = new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ConnectionString;
        await ExecuteAsync(connection, "CREATE TABLE t (id INTEGER PRIMARY KEY)");
        var env = new Dictionary<string, string?> { ["DB"] = connection };
        var output = Path.Combine(_directory, "safe.vqschema.json");
        await RunAsync(env, "schema", "snapshot", "--provider", "sqlite", "--connection-env", "DB", "--output", output);
        await ExecuteAsync(connection, "CREATE TABLE extra (id INTEGER PRIMARY KEY)");

        var (code, _, _) = await RunAsync(env, "schema", "diff", "--snapshot", output, "--provider", "sqlite", "--connection-env", "DB");
        Assert.Equal(CliApp.Ok, code);
        (code, _, _) = await RunAsync(env, "schema", "diff", "--snapshot", output, "--provider", "sqlite", "--connection-env", "DB", "--fail-on", "any");
        Assert.Equal(CliApp.Differences, code);
    }

    [Theory]
    [InlineData(new[] { "schema", "snapshot", "--connection-env", "X" }, "--provider is required")]
    [InlineData(new[] { "schema", "snapshot", "--provider", "oracle", "--connection-env", "X" }, "--provider is required")]
    [InlineData(new[] { "schema", "snapshot", "--provider", "sqlite", "--connection-env", "MISSING" }, "is not set")]
    [InlineData(new[] { "schema", "snapshot", "--provider", "sqlite", "--bogus", "1" }, "Unknown option '--bogus'")]
    [InlineData(new[] { "schema", "explode" }, "Unknown command")]
    public async Task Usage_errors_exit_with_code_2(string[] args, string message)
    {
        var (code, _, stderr) = await RunAsync(new Dictionary<string, string?>(), args);
        Assert.Equal(CliApp.UsageError, code);
        Assert.Contains(message, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connection_failures_never_print_the_connection_string()
    {
        const string Secret = "Sup3rS3cret!";
        var env = new Dictionary<string, string?> { ["DB"] = $"Host=127.0.0.1;Port=1;Username=u;Password={Secret};Timeout=2" };
        var (code, stdout, stderr) = await RunAsync(env, "schema", "snapshot", "--provider", "postgresql", "--connection-env", "DB", "--output", Path.Combine(_directory, "x.vqschema.json"));
        Assert.Equal(CliApp.DatabaseError, code);
        Assert.DoesNotContain(Secret, stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("Could not read the schema", stderr, StringComparison.Ordinal);
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(Dictionary<string, string?> environment, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await CliApp.RunAsync(args, stdout, stderr, name => environment.GetValueOrDefault(name), TestContext.Current.CancellationToken);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}

/// <summary>Runs the built <c>velquery</c> executable, so runtime configuration problems surface (not only the in-process API).</summary>
public sealed class ToolProcessTests
{
    [Theory]
    [InlineData("sqlserver", "Server=127.0.0.1,1;User ID=sa;Password=Pr0cessS3cret;Connect Timeout=2;TrustServerCertificate=true", "SqlException")]
    [InlineData("postgresql", "Host=127.0.0.1;Port=1;Username=u;Password=Pr0cessS3cret;Timeout=2", "NpgsqlException")]
    [InlineData("mysql", "Server=127.0.0.1;Port=1;User ID=u;Password=Pr0cessS3cret;Connection Timeout=2", "MySqlException")]
    public async Task Unreachable_server_exits_with_code_3_without_leaking_the_password(string provider, string connectionString, string driverException)
    {
        var tool = Path.Combine(AppContext.BaseDirectory, "velquery.dll");
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment = { ["VQ_TEST_DB"] = connectionString },
        };
        foreach (var arg in new[] { tool, "schema", "snapshot", "--provider", provider, "--connection-env", "VQ_TEST_DB", "--output", Path.Combine(Path.GetTempPath(), "never.vqschema.json") })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(process.ExitCode == CliApp.DatabaseError, $"exit {process.ExitCode}: {stderr}");
        // The driver's own connection error proves the tool's runtime configuration lets the driver run at all
        // (for example SqlClient rejects invariant globalization with NotSupportedException before connecting).
        Assert.StartsWith($"Could not read the schema ({driverException})", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Pr0cessS3cret", stdout + stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal); // no stack trace
    }
}
