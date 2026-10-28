using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VelQuery.Cli.Schema;
using VelQuery.Testing;
using Xunit;

namespace VelQuery.Cli.Tests;

/// <summary>
/// One contract to check against an exported snapshot and then run against the real database. <see cref="Expected"/>
/// lists the VelQuery diagnostic IDs the build must report for it (empty: none). With <see cref="Procedure"/> set,
/// <see cref="Sql"/> is a stored procedure name called with those parameters (<c>CommandType.StoredProcedure</c>).
/// </summary>
public sealed record QueryCase(string Name, string Sql, string ReturnType, string[] Expected, string? Cardinality = null, string[]? Procedure = null)
{
    /// <summary>Diagnostics that mean the database itself will reject the statement.</summary>
    public bool MustFail => Expected.Any(id => id is "VQ1005" or "VQ1029" or "VQ1030");

    public override string ToString() => Name;
}

/// <summary>
/// End-to-end proof for the schema checks (ADR-0006): the snapshot exported from a live database by the CLI is fed to
/// the real generator together with a set of contracts, and every contract is then executed against that database
/// (inside a rolled-back transaction). A contract the checker accepts must run; a contract it rejects with an
/// unknown-name, required-column or generated-column diagnostic must fail. So a passing run shows the checks report
/// neither false positives nor false negatives for these queries on the real engine.
/// </summary>
internal static partial class SchemaScenario
{
    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using VelQuery;

        """;

    public static async Task RunAsync(string dialect, DbSchema schema, string rowTypes, IReadOnlyList<QueryCase> cases, Func<Task<DbConnection>> connect, IReadOnlyDictionary<string, object> values)
    {
        var snapshot = schema.ToCanonicalJson();
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var actual = Diagnose(dialect, snapshot, rowTypes, c);
            if (!actual.SequenceEqual(c.Expected.Order(StringComparer.Ordinal)))
            {
                failures.Add($"{c.Name}: expected [{string.Join(", ", c.Expected)}] but the build reported [{string.Join(", ", actual)}]\n    SQL: {c.Sql}\n    {string.Join("\n    ", Messages(dialect, snapshot, rowTypes, c))}");
                continue;
            }

            var error = await ExecuteAsync(connect, c.Sql, values, c.Procedure);
            if (c.MustFail && error is null)
            {
                failures.Add($"{c.Name}: the build reported [{string.Join(", ", actual)}] but the database ran the statement\n    SQL: {c.Sql}");
            }
            else if (!c.MustFail && error is not null)
            {
                failures.Add($"{c.Name}: the build accepted it but the database rejected it: {error}\n    SQL: {c.Sql}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static string[] Diagnose(string dialect, string snapshot, string rowTypes, QueryCase c)
        => Run(dialect, snapshot, rowTypes, c).Select(d => d.Id).Order(StringComparer.Ordinal).ToArray();

    private static IEnumerable<string> Messages(string dialect, string snapshot, string rowTypes, QueryCase c)
        => Run(dialect, snapshot, rowTypes, c).Select(d => d.Id + ": " + d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));

    private static IEnumerable<Microsoft.CodeAnalysis.Diagnostic> Run(string dialect, string snapshot, string rowTypes, QueryCase c)
    {
        var parameters = string.Concat((c.Procedure ?? Placeholders(c.Sql)).Select(p => $", {ParameterType(p)} {p}"));
        var cardinality = (c.Cardinality is null ? string.Empty : ", Cardinality = QueryCardinality." + c.Cardinality)
            + (c.Procedure is null ? string.Empty : ", CommandType = System.Data.CommandType.StoredProcedure");
        var source = Usings + rowTypes + $$"""

            [VelQuerySql("q.{{c.Name}}", {{Literal(c.Sql)}}{{cardinality}})]
            public static partial class Q
            {
                public static partial {{c.ReturnType}} ExecuteAsync(IVelQuerySession session{{parameters}});
            }
            """;
        var compilation = GeneratorHarness.Compile(source);
        return GeneratorHarness.GetVelQueryDiagnostics(compilation, dialect, [("/db/shop.vqschema.json", snapshot)])
            .Where(d => d.Id.StartsWith("VQ", StringComparison.Ordinal));
    }

    private static async Task<string?> ExecuteAsync(Func<Task<DbConnection>> connect, string sql, IReadOnlyDictionary<string, object> values, string[]? procedure)
    {
        await using var connection = await connect();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            if (procedure is not null)
            {
                command.CommandType = System.Data.CommandType.StoredProcedure;
            }

            foreach (var name in procedure ?? Placeholders(sql))
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = procedure is null ? "@" + name : name;
                parameter.Value = values[name];
                command.Parameters.Add(parameter);
            }

            await using (var reader = await command.ExecuteReaderAsync())
            {
                do
                {
                    while (await reader.ReadAsync())
                    {
                    }
                }
                while (await reader.NextResultAsync());
            }

            return null;
        }
        catch (Exception ex) when (ex is DbException or ArgumentException or InvalidOperationException)
        {
            return ex.Message;
        }
        finally
        {
            try
            {
                await transaction.RollbackAsync();
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                // The server may already have aborted the transaction after the error.
            }
        }
    }

    /// <summary>Parameters to bind: <c>@name</c> placeholders except T-SQL variables the batch declares itself.</summary>
    private static IEnumerable<string> Placeholders(string sql)
    {
        var declared = DeclaredPattern().Matches(sql).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return PlaceholderPattern().Matches(sql).Select(m => m.Groups[1].Value).Where(n => !declared.Contains(n)).Distinct(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"DECLARE\s+@(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex DeclaredPattern();

    private static string ParameterType(string name) => name switch
    {
        "total" or "min" => "decimal",
        "id" or "p_id" => "int",
        _ => "string",
    };

    private static string Literal(string sql) => "\"\"\"\n" + sql + "\n\"\"\"";

    [GeneratedRegex(@"(?<![@\w])@([A-Za-z_]\w*)")]
    private static partial Regex PlaceholderPattern();
}
