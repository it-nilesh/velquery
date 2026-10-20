using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace VelQuery.MongoDB.Generators.Tests;

/// <summary>Build-time diagnostics VQ2001–VQ2016 (ADR-0017), including the NoSQL-injection rules.</summary>
public sealed class DiagnosticTests
{
    private const string FindUsers = "public static partial Task<IReadOnlyList<User>> ExecuteAsync(VelQueryMongoSession session, string name, CancellationToken cancellationToken);";

    private static Diagnostic[] Run(string attribute, string method = FindUsers, string types = "")
    {
        t_lastSource = MongoGeneratorHarness.Contract(attribute, method, types);
        return MongoGeneratorHarness.Diagnostics(t_lastSource).ToArray();
    }

    private static Diagnostic Single(string id, string attribute, string method = FindUsers, string types = "")
    {
        var diagnostics = Run(attribute, method, types);
        Assert.True(diagnostics.Count(d => d.Id == id) == 1, $"expected one {id}, got: {string.Join(" | ", diagnostics.Select(d => d.ToString()))}");
        return diagnostics.Single(d => d.Id == id);
    }

    private static void Clean(string attribute, string method = FindUsers, string types = "")
    {
        var source = MongoGeneratorHarness.Contract(attribute, method, types);
        var (result, output) = MongoGeneratorHarness.Run(MongoGeneratorHarness.Compile(source));
        Assert.Empty(result.Diagnostics);
        Assert.Empty(MongoGeneratorHarness.CompilerProblems(output));
    }

    [ThreadStatic]
    private static string? t_lastSource;

    // Generator diagnostics use path-based locations (no SourceTree), so slice the source that produced them.
    private static string SourceAt(Diagnostic diagnostic)
        => t_lastSource!.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);

    [Fact]
    public void Valid_contract_has_no_diagnostics()
        => Clean(""""[VelQueryMongoFind("users.byName", "users", """{ "Name": @name }""")]"""");

    // ---- VQ2001 / VQ2002: placeholders vs. parameters (VQ-DB-006) ----

    [Fact]
    public void VQ2001_unknown_placeholder_points_at_the_placeholder()
    {
        var diagnostic = Single("VQ2001", """"[VelQueryMongoFind("users.byName", "users", """{ "Name": @nmae }""")]"""");
        Assert.Equal("@nmae", SourceAt(diagnostic));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void VQ2002_unused_parameter_is_a_warning()
    {
        var diagnostics = Run(""""[VelQueryMongoFind("users.all", "users", """{ }""")]"""");
        var unused = Assert.Single(diagnostics);
        Assert.Equal("VQ2002", unused.Id);
        Assert.Equal(DiagnosticSeverity.Warning, unused.Severity);
    }

    [Fact]
    public void A_quoted_at_name_is_an_ordinary_string_not_a_placeholder()
    {
        var diagnostics = Run(""""[VelQueryMongoFind("users.literal", "users", """{ "Name": "@name" }""")]"""");
        Assert.Equal("VQ2002", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public void Placeholders_match_parameters_case_insensitively()
        => Clean(""""[VelQueryMongoFind("users.byName", "users", """{ "Name": @Name }""")]"""");

    // ---- VQ2003 / VQ2004: document types ----

    [Theory]
    [InlineData("public sealed record Bad(int Id, Dictionary<string, int> Map);", "Dictionary")]
    [InlineData("public sealed record Bad(int Id, DateTimeOffset At);", "DateTimeOffset")]
    [InlineData("public sealed record Bad(int Id, float Ratio);", "float")]
    [InlineData("public enum Small : byte { A } public sealed record Bad(int Id, Small Size);", "enum")]
    [InlineData("public sealed record Bad(int Id, object Anything);", "object")]
    [InlineData("public sealed record Bad(int Id, [property: BsonRepresentation(BsonType.String)] int Code);", "BsonRepresentation")]
    [InlineData("[BsonDiscriminator(\"bad\")] public sealed record Bad(int Id);", "BsonDiscriminator")]
    [InlineData("public abstract class Bad { public int Id { get; set; } }", "abstract")]
    [InlineData("public sealed record Bad(int Id, Inner? Child); public struct Inner { public int X { get; set; } }", "nullable struct")]
    public void VQ2003_unsupported_document_types_are_reported(string types, string expected)
    {
        var diagnostic = Single("VQ2003", """"[VelQueryMongoFind("bad.all", "bad", """{ }""")]"""",
            "public static partial Task<IReadOnlyList<Bad>> ExecuteAsync(VelQueryMongoSession session, CancellationToken cancellationToken);", types);
        Assert.Contains(expected, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void VQ2004_two_members_mapping_to_one_field_are_ambiguous()
    {
        var diagnostic = Single("VQ2004", """"[VelQueryMongoFind("bad.all", "bad", """{ }""")]"""",
            "public static partial Task<IReadOnlyList<Bad>> ExecuteAsync(VelQueryMongoSession session, CancellationToken cancellationToken);",
            "public sealed class Bad { public int Id { get; set; } [BsonElement(\"x\")] public int A { get; set; } [BsonElement(\"x\")] public int B { get; set; } }");
        Assert.Contains("'x'", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void Recursive_document_types_are_supported()
        => Clean(""""[VelQueryMongoFind("tree.all", "tree", """{ }""")]"""",
            "public static partial Task<IReadOnlyList<Node>> ExecuteAsync(VelQueryMongoSession session, CancellationToken cancellationToken);",
            "public sealed record Node(int Id, List<Node> Children, Node? Parent);");

    // ---- VQ2005: malformed JSON never crashes the generator ----

    [Theory]
    [InlineData("""{ "Name": }""")]
    [InlineData("""{ Name: @name }""")]
    [InlineData("""{ "Name": @name,, }""")]
    [InlineData("""{ 'Name': @name }""")]
    [InlineData("""{ "Name": "unterminated }""")]
    [InlineData("""{ "Name": @name } trailing""")]
    [InlineData("""{ "Name": tru }""")]
    [InlineData("""{ "Name": 01 }""")]
    [InlineData("""{ "Name": @1x }""")]
    [InlineData("""{ "Name": "\q" }""")]
    [InlineData("")]
    public void VQ2005_malformed_json_is_reported(string json)
    {
        var diagnostics = MongoGeneratorHarness.Diagnostics(MongoGeneratorHarness.Contract($"[VelQueryMongoFind(\"users.bad\", \"users\", {Quote(json)})]", FindUsers));
        Assert.Contains(diagnostics, d => d.Id is "VQ2005" or "VQ2014");
        Assert.DoesNotContain(diagnostics, d => d.Id == "VQ2017");
    }

    [Fact]
    public void VQ2005_deep_nesting_is_bounded()
    {
        var json = new string('[', 5_000) + new string(']', 5_000);
        var diagnostics = MongoGeneratorHarness.Diagnostics(MongoGeneratorHarness.Contract($"[VelQueryMongoAggregate(\"deep\", \"users\", {Quote(json)})]", FindUsers));
        Assert.Contains(diagnostics, d => d.Id == "VQ2005" && d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("nesting", StringComparison.Ordinal));
    }

    // ---- VQ2006 / VQ2007 / VQ2008: the injection guarantee ----

    [Theory]
    [InlineData("""{ @name: 1 }""")]
    [InlineData("""{ "Age": { @name: 5 } }""")]
    [InlineData("""{ "$and": [ { @name: 1 } ] }""")]
    public void VQ2006_placeholders_cannot_be_keys_or_operators(string json)
    {
        var diagnostic = Single("VQ2006", $"[VelQueryMongoFind(\"users.bad\", \"users\", {Quote(json)})]");
        Assert.Equal("@name", SourceAt(diagnostic));
    }

    [Theory]
    [InlineData("""{ "$where": @name }""", "$where")]
    [InlineData("""{ "$expr": { "$function": { "body": @name, "args": [], "lang": "js" } } }""", "$function")]
    [InlineData("""{ "$expr": { "$eq": [ { "$getField": @name }, 1 ] } }""", "$getField")]
    [InlineData("""{ "$jsonSchema": { "required": [ @name ] } }""", "$jsonSchema")]
    public void VQ2007_placeholders_are_rejected_where_values_are_code_or_identifiers(string json, string where)
    {
        var diagnostic = Single("VQ2007", $"[VelQueryMongoFind(\"users.bad\", \"users\", {Quote(json)})]");
        Assert.Contains(where, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Equal("@name", SourceAt(diagnostic));
    }

    [Theory]
    [InlineData("Sort", """{ "Name": @name }""")]
    [InlineData("Projection", """{ "Name": @name }""")]
    public void VQ2007_sort_and_projection_are_constant(string property, string json)
        => Single("VQ2007", $"[VelQueryMongoFind(\"users.bad\", \"users\", \"{{ }}\", {property} = {Quote(json)})]");

    [Theory]
    [InlineData("""[ { "$lookup": { "from": @name, "localField": "a", "foreignField": "b", "as": "c" } } ]""", "$lookup")]
    [InlineData("""[ { "$unwind": @name } ]""", "$unwind")]
    [InlineData("""[ { "$sort": { "Name": @name } } ]""", "$sort")]
    [InlineData("""[ { "$group": { "_id": null, "x": { "$accumulator": { "init": @name } } } } ]""", "$accumulator")]
    public void VQ2007_pipeline_stages_that_take_identifiers_reject_placeholders(string json, string where)
    {
        var diagnostic = Single("VQ2007", $"[VelQueryMongoAggregate(\"users.bad\", \"users\", {Quote(json)})]");
        Assert.Contains(where, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void VQ2007_rename_targets_are_identifiers()
        => Single("VQ2007", """"[VelQueryMongoUpdate("users.rename", "users", """{ "_id": 1 }""", """{ "$rename": { "Name": @name } }""")]"""",
            "public static partial Task<VelQueryMongoUpdateResult> ExecuteAsync(VelQueryMongoSession session, string name);");

    [Theory]
    [InlineData("BsonDocument")]
    [InlineData("BsonValue")]
    [InlineData("object")]
    [InlineData("User")]
    [InlineData("Dictionary<string, object>")]
    [InlineData("IEnumerable<BsonDocument>")]
    public void VQ2008_documents_and_untyped_values_cannot_be_bound(string type)
    {
        var diagnostic = Single("VQ2008", """"[VelQueryMongoFind("users.bad", "users", """{ "Name": @name }""")]"""",
            $"public static partial Task<IReadOnlyList<User>> ExecuteAsync(VelQueryMongoSession session, {type} name);");
        Assert.Contains("name", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void VQ2008_limit_stage_requires_an_integer_argument()
        => Single("VQ2008", """"[VelQueryMongoAggregate("users.top", "users", """[ { "$limit": @name } ]""")]"""");

    [Fact]
    public void Sequences_of_scalars_can_be_bound()
        => Clean(""""[VelQueryMongoFind("users.in", "users", """{ "Name": { "$in": @names }, "Age": { "$in": @ages } }""")]"""",
            "public static partial Task<IReadOnlyList<User>> ExecuteAsync(VelQueryMongoSession session, string[] names, List<int?> ages);");

    // ---- VQ2009: Extended JSON ----

    [Theory]
    [InlineData("""{ "_id": { "$oid": "zz" } }""")]
    [InlineData("""{ "_id": { "$oid": @name } }""")]
    [InlineData("""{ "At": { "$date": "2026-01-01" } }""")]
    [InlineData("""{ "At": { "$date": "2026-01-01T00:00:00.0001Z" } }""")]
    [InlineData("""{ "Code": { "$code": "function() {}" } }""")]
    [InlineData("""{ "N": { "$numberInt": "3000000000" } }""")]
    [InlineData("""{ "N": { "$numberLong": "1", "extra": 1 } }""")]
    [InlineData("""{ "N": 99999999999999999999 }""")]
    [InlineData("""{ "B": { "$binary": { "base64": "!!", "subType": "00" } } }""")]
    [InlineData("""{ "U": { "$uuid": "not-a-uuid" } }""")]
    public void VQ2009_invalid_extended_json_is_reported(string json)
        => Assert.Contains(MongoGeneratorHarness.Diagnostics(MongoGeneratorHarness.Contract($"[VelQueryMongoFind(\"users.bad\", \"users\", {Quote(json)})]", FindUsers)), d => d.Id == "VQ2009");

    [Fact]
    public void Valid_extended_json_wrappers_compile()
        => Clean(""""
            [VelQueryMongoFind("users.ext", "users", """
                { "Name": @name,
                  "_id": { "$oid": "65f0c0ffee0000000000abcd" },
                  "At": { "$date": "2026-01-01T10:00:00.123+05:30" },
                  "At2": { "$date": { "$numberLong": "-1" } },
                  "I": { "$numberInt": "-7" }, "L": { "$numberLong": "9223372036854775807" },
                  "D": { "$numberDouble": "-Infinity" }, "M": { "$numberDecimal": "1.000000000000000000000000000000001" },
                  "U": { "$uuid": "a3c1d865-9684-46a4-b000-000000000001" },
                  "B": { "$binary": { "base64": "AP8Q", "subType": "80" } },
                  "R": { "$regularExpression": { "pattern": "^a\\d", "options": "im" } },
                  "T": { "$timestamp": { "t": 4294967295, "i": 1 } },
                  "Min": { "$minKey": 1 }, "Max": { "$maxKey": 1 },
                  "F": 1.5e-7, "Neg": -0.0, "Big": 9007199254740993, "Text": "éé\t\"q\"" }
                """)]
            """");

    // ---- VQ2010 – VQ2016: contract shape ----

    [Fact]
    public void VQ2010_contract_type_must_be_partial()
    {
        var source = MongoGeneratorHarness.Contract("", "").Replace("public static partial class Contract", "[VelQueryMongoFind(\"q\", \"users\", \"{ }\")] public static class NotPartial { } public static partial class Contract", StringComparison.Ordinal);
        Assert.Contains(MongoGeneratorHarness.Diagnostics(source), d => d.Id == "VQ2010");
    }

    [Fact]
    public void VQ2011_contract_must_declare_exactly_one_partial_method()
        => Single("VQ2011", """[VelQueryMongoFind("q", "users", "{ }")]""", "public static int Helper() => 1;");

    [Theory]
    [InlineData("public static partial Task<IReadOnlyList<User>> ExecuteAsync(string name);")]
    [InlineData("public static partial Task<IReadOnlyList<User>> ExecuteAsync(VelQueryMongoSession session, CancellationToken cancellationToken, string name);")]
    [InlineData("public static partial Task<IReadOnlyList<User>> ExecuteAsync(VelQueryMongoSession session, ref string name);")]
    [InlineData("static partial Task<IReadOnlyList<User>> ExecuteAsync(VelQueryMongoSession session, string name);")]
    public void VQ2012_invalid_signatures_are_reported(string method)
        => Assert.Contains(MongoGeneratorHarness.Diagnostics(MongoGeneratorHarness.Contract(""""[VelQueryMongoFind("q", "users", """{ "Name": @name }""")]"""", method)), d => d.Id == "VQ2012");

    [Theory]
    [InlineData("""[VelQueryMongoFind("q", "users", "{ }")]""", "public static partial Task<int> ExecuteAsync(VelQueryMongoSession session);")]
    [InlineData("""[VelQueryMongoFind("q", "users", "{ }", Cardinality = QueryCardinality.FirstOrDefault)]""", "public static partial Task<User> ExecuteAsync(VelQueryMongoSession session);")]
    [InlineData("""[VelQueryMongoFind("q", "users", "{ }", Cardinality = QueryCardinality.First)]""", "public static partial Task<IReadOnlyList<User>> ExecuteAsync(VelQueryMongoSession session);")]
    [InlineData("""[VelQueryMongoCount("q", "users", "{ }")]""", "public static partial Task<int> ExecuteAsync(VelQueryMongoSession session);")]
    [InlineData("""[VelQueryMongoDelete("q", "users", "{ }")]""", "public static partial Task ExecuteAsync(VelQueryMongoSession session);")]
    [InlineData("""[VelQueryMongoUpdate("q", "users", "{ }", "{ \"$set\": { \"a\": 1 } }")]""", "public static partial Task<long> ExecuteAsync(VelQueryMongoSession session);")]
    [InlineData("""[VelQueryMongoInsert("q", "users")]""", "public static partial Task<long> ExecuteAsync(VelQueryMongoSession session, User user);")]
    [InlineData("""[VelQueryMongoFind("q", "users", "{ }")]""", "public static partial Task<Point> ExecuteAsync(VelQueryMongoSession session);")]
    public void VQ2013_unsupported_return_types_are_reported(string attribute, string method)
        => Assert.Contains(Run(attribute, method, "public struct Point { public int X { get; set; } }"), d => d.Id == "VQ2013");

    [Theory]
    [InlineData("""[VelQueryMongoFind("", "users", "{ }")]""")]
    [InlineData("""[VelQueryMongoFind("q", "us$ers", "{ }")]""")]
    [InlineData("""[VelQueryMongoFind("q", "system.users", "{ }")]""")]
    [InlineData("""[VelQueryMongoFind("q", "users", "{ }", Limit = -1)]""")]
    [InlineData("""[VelQueryMongoFind("q", "users", "[ ]")]""")]
    [InlineData("""[VelQueryMongoFind("q", "users", "{ }", Cardinality = QueryCardinality.Scalar)]""")]
    [InlineData("""[VelQueryMongoAggregate("q", "users", "{ }")]""")]
    [InlineData("""[VelQueryMongoAggregate("q", "users", "[ { \"$match\": { }, \"$sort\": { } } ]")]""")]
    [InlineData("""[VelQueryMongoAggregate("q", "users", "[ { \"$out\": \"copy\" } ]")]""")]
    [InlineData("""[VelQueryMongoAggregate("q", "users", "[ { \"$merge\": { \"into\": \"copy\" } } ]")]""")]
    public void VQ2014_invalid_contract_arguments_are_reported(string attribute)
        => Assert.Contains(Run(attribute, "public static partial Task<IReadOnlyList<User>> ExecuteAsync(VelQueryMongoSession session);"), d => d.Id == "VQ2014");

    [Fact]
    public void VQ2014_update_documents_must_use_operators()
        => Single("VQ2014", """[VelQueryMongoUpdate("q", "users", "{ }", "{ \"Name\": \"replaced\" }")]""",
            "public static partial Task<VelQueryMongoUpdateResult> ExecuteAsync(VelQueryMongoSession session);");

    [Fact]
    public void VQ2015_parameters_differing_only_in_case_are_duplicates()
        => Single("VQ2015", """"[VelQueryMongoFind("q", "users", """{ "Name": @name }""")]"""",
            "public static partial Task<IReadOnlyList<User>> ExecuteAsync(VelQueryMongoSession session, string name, string Name);");

    [Fact]
    public void VQ2016_duplicate_keys_are_reported()
    {
        var diagnostic = Single("VQ2016", """"[VelQueryMongoFind("q", "users", """{ "Name": @name, "Name": "x" }""")]"""");
        Assert.Equal("\"Name\"", SourceAt(diagnostic));
    }

    private static string Quote(string json) => "\"" + json.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
