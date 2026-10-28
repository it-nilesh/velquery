using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Cli.Schema;

namespace VelQuery.Cli;

/// <summary>Entry point of the <c>velquery</c> tool.</summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        return await CliApp.RunAsync(args, Console.Out, Console.Error, Environment.GetEnvironmentVariable, cancellation.Token).ConfigureAwait(false);
    }
}

/// <summary>
/// The <c>velquery</c> commands (PRD §17.1). <c>schema snapshot</c> writes an offline snapshot for build-time SQL
/// checks; <c>schema diff</c> compares an approved snapshot with a live database for CI drift checks (VQ-FR-039).
/// Connection strings are never printed or written to files (PRD §17.1 safety gates).
/// </summary>
internal static class CliApp
{
    public const int Ok = 0;
    public const int Differences = 1;
    public const int UsageError = 2;
    public const int DatabaseError = 3;

    private const string DefaultOutput = "velquery.vqschema.json";

    private const string Usage = """
        Usage:
          velquery schema snapshot --provider <name> --connection-env <VAR> [--output <file>] [--schema <a,b>] [--name <logical>] [--check]
          velquery schema diff --snapshot <file> --provider <name> --connection-env <VAR> [--schema <a,b>] [--fail-on breaking|any]

        Providers: sqlserver, postgresql, mysql (also MariaDB), sqlite.
        Connection: --connection-env names an environment variable holding the connection string (recommended);
                    --connection <string> passes it directly (visible in shell history and process lists).
        Use a read-only account: only catalog views are queried, never table data.

        snapshot   Writes the snapshot (default: velquery.vqschema.json). Put it in the project that declares the
                   [VelQuerySql] contracts; *.vqschema.json files there are picked up by the build automatically.
          --check  Do not write; exit 1 if the file differs from the database (CI drift check).
        diff       Lists changes between the snapshot and the database. Exit code 1 when a breaking change is
                   found (or any change with --fail-on any).

        Exit codes: 0 success, 1 differences found, 2 usage error, 3 database error.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, Func<string, string?> environment, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return args.Length == 0 ? UsageError : Ok;
        }

        if (args.Length < 2 || args[0] != "schema" || args[1] is not ("snapshot" or "diff"))
        {
            await error.WriteLineAsync("Unknown command. Run 'velquery --help'.").ConfigureAwait(false);
            return UsageError;
        }

        if (!TryParseOptions(args.Skip(2).ToArray(), out var options, out var problem))
        {
            await error.WriteLineAsync(problem).ConfigureAwait(false);
            return UsageError;
        }

        var provider = SchemaReaders.Normalize(options.GetValueOrDefault("provider"));
        if (provider is null)
        {
            await error.WriteLineAsync($"--provider is required: {string.Join(", ", SchemaReaders.Providers)}.").ConfigureAwait(false);
            return UsageError;
        }

        string? connectionString;
        if (options.TryGetValue("connection-env", out var variable))
        {
            connectionString = environment(variable);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                await error.WriteLineAsync($"Environment variable '{variable}' is not set.").ConfigureAwait(false);
                return UsageError;
            }
        }
        else if (!options.TryGetValue("connection", out connectionString) || string.IsNullOrWhiteSpace(connectionString))
        {
            await error.WriteLineAsync("Pass --connection-env <VAR> (or --connection <string>).").ConfigureAwait(false);
            return UsageError;
        }

        var schemas = options.TryGetValue("schema", out var list)
            ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        DbSchema current;
        try
        {
            current = await SchemaReaders.ReadAsync(provider, connectionString, schemas, options.GetValueOrDefault("name"), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The driver message names the failure (login, host, timeout); it never contains the password.
            await error.WriteLineAsync($"Could not read the schema ({ex.GetType().Name}): {ex.Message}").ConfigureAwait(false);
            return DatabaseError;
        }

        return args[1] == "snapshot"
            ? await SnapshotAsync(current, options, output, error, cancellationToken).ConfigureAwait(false)
            : await DiffAsync(current, options, output, error, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> SnapshotAsync(DbSchema current, Dictionary<string, string> options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var path = options.GetValueOrDefault("output") ?? DefaultOutput;
        var json = current.ToCanonicalJson();
        var columns = current.Objects.Sum(o => o.Columns.Count);
        if (options.ContainsKey("check"))
        {
            if (!File.Exists(path))
            {
                await error.WriteLineAsync($"{path} does not exist; run 'velquery schema snapshot' without --check first.").ConfigureAwait(false);
                return Differences;
            }

            var existing = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Replace("\r\n", "\n", StringComparison.Ordinal);
            if (existing == json)
            {
                await output.WriteLineAsync($"{path} is up to date ({current.Objects.Count} objects, {columns} columns).").ConfigureAwait(false);
                return Ok;
            }

            await output.WriteLineAsync($"{path} is out of date.").ConfigureAwait(false);
            await PrintChangesAsync(output, SafeParse(existing) is { } approved ? SchemaDiff.Compare(approved, current) : []).ConfigureAwait(false);
            return Differences;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Wrote {path}: {current.Objects.Count} objects, {columns} columns ({current.Dialect}).").ConfigureAwait(false);
        return Ok;
    }

    private static async Task<int> DiffAsync(DbSchema current, Dictionary<string, string> options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!options.TryGetValue("snapshot", out var path) || !File.Exists(path))
        {
            await error.WriteLineAsync("--snapshot <file> is required and must exist.").ConfigureAwait(false);
            return UsageError;
        }

        var approved = SafeParse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        if (approved is null)
        {
            await error.WriteLineAsync($"{path} is not a valid {DbSchema.Format} snapshot.").ConfigureAwait(false);
            return UsageError;
        }

        var failOn = options.GetValueOrDefault("fail-on") ?? "breaking";
        if (failOn is not ("breaking" or "any"))
        {
            await error.WriteLineAsync("--fail-on must be 'breaking' or 'any'.").ConfigureAwait(false);
            return UsageError;
        }

        var changes = SchemaDiff.Compare(approved, current);
        await PrintChangesAsync(output, changes).ConfigureAwait(false);
        return changes.Any(c => c.Breaking) || (failOn == "any" && changes.Count > 0) ? Differences : Ok;
    }

    private static async Task PrintChangesAsync(TextWriter output, List<SchemaChange> changes)
    {
        if (changes.Count == 0)
        {
            await output.WriteLineAsync("No schema changes.").ConfigureAwait(false);
            return;
        }

        var breaking = changes.Count(c => c.Breaking);
        await output.WriteLineAsync($"{changes.Count} change(s), {breaking} breaking:").ConfigureAwait(false);
        foreach (var change in changes.OrderByDescending(c => c.Breaking).ThenBy(c => c.Target, StringComparer.Ordinal))
        {
            var detail = change.Detail is null ? string.Empty : " (" + change.Detail + ")";
            await output.WriteLineAsync($"  {(change.Breaking ? "BREAKING" : "ok      ")}  {change.Kind,-20} {change.Target}{detail}").ConfigureAwait(false);
        }
    }

    private static DbSchema? SafeParse(string json)
    {
        try
        {
            return DbSchema.Parse(json);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static bool TryParseOptions(string[] args, out Dictionary<string, string> options, out string problem)
    {
        options = new Dictionary<string, string>(StringComparer.Ordinal);
        problem = string.Empty;
        string[] flags = ["check"];
        string[] known = ["provider", "connection-env", "connection", "output", "schema", "name", "snapshot", "fail-on", .. flags];
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || !known.Contains(args[i][2..]))
            {
                problem = $"Unknown option '{args[i]}'. Run 'velquery --help'.";
                return false;
            }

            var name = args[i][2..];
            if (flags.Contains(name))
            {
                options[name] = "true";
                continue;
            }

            if (i + 1 >= args.Length)
            {
                problem = $"Option '--{name}' needs a value.";
                return false;
            }

            options[name] = args[++i];
        }

        return true;
    }
}
