using Xunit;

namespace VelQuery.Analyzers.Tests;

/// <summary>Location-aware generator diagnostics for [VelQuerySql] contracts (PRD §7.1, VQ-FR-033, VQ-FR-034).</summary>
public sealed class ContractDiagnosticsTests
{
    private const string UserRow = "public sealed record UserRow(long Id, string Name, string? Email);\n";

    [Fact]
    public void Valid_contract_reports_nothing()
        => DiagnosticVerifier.Verify(UserRow + """
            [VelQuerySql("users.by-id", "SELECT Id, Name, Email FROM dbo.Users WHERE Id = @Id")]
            public static partial class UsersById
            {
                public static partial Task<UserRow?> ExecuteAsync(IVelQuerySession session, long id, CancellationToken cancellationToken = default);
            }
            """);

    [Fact]
    public void VQ1001_points_at_the_undeclared_placeholder()
        => DiagnosticVerifier.Verify(UserRow + """
            [VelQuerySql("users.by-id", "SELECT Id, Name, Email FROM dbo.Users WHERE Id = @Id AND Name = {|VQ1001:@Nmae|}")]
            public static partial class UsersById
            {
                public static partial Task<UserRow?> ExecuteAsync(IVelQuerySession session, long id, CancellationToken cancellationToken = default);
            }
            """);

    [Fact]
    public void VQ1001_in_verbatim_and_raw_literals_points_at_the_placeholder()
    {
        const string Raw = "\"\"\"";
        const string Method = " { public static partial Task<long> ExecuteAsync(IVelQuerySession session); }\n";
        DiagnosticVerifier.Verify(
            "[VelQuerySql(\"a\", @\"SELECT Id FROM T WHERE Id = {|VQ1001:@Missing|}\")]\npublic static partial class A" + Method
            + "[VelQuerySql(\"b\", " + Raw + " SELECT Id FROM T WHERE Id = {|VQ1001:@Missing|} " + Raw + ")]\npublic static partial class B" + Method
            // Indented multi-line raw strings strip indentation, so offsets cannot be mapped: the whole argument is used.
            + "[VelQuerySql(\"c\", {|VQ1001:" + Raw + "\n    SELECT Id\n    FROM T WHERE Id = @Missing\n    " + Raw + "|})]\npublic static partial class C" + Method);
    }

    [Fact]
    public void VQ1001_with_escaped_literal_falls_back_to_the_whole_argument()
        => DiagnosticVerifier.Verify(UserRow + """
            [VelQuerySql("users.q", {|VQ1001:"SELECT [Id] FROM T WHERE Name = 'a\tb' AND Id = @Missing"|})]
            public static partial class Q
            {
                public static partial Task<long> ExecuteAsync(IVelQuerySession session);
            }
            """);

    [Fact]
    public void VQ1001_when_sql_is_a_constant_reference_points_at_the_argument()
        => DiagnosticVerifier.Verify(UserRow + """
            public static class Sql { public const string Users = "SELECT Id FROM T WHERE Id = @Missing"; }

            [VelQuerySql("users.q", {|VQ1001:Sql.Users|})]
            public static partial class Q
            {
                public static partial Task<long> ExecuteAsync(IVelQuerySession session);
            }
            """);

    [Fact]
    public void Placeholders_in_literals_identifiers_comments_and_system_variables_are_ignored()
        => DiagnosticVerifier.Verify(UserRow + """
            [VelQuerySql("users.q", "SELECT [@a], \"@b\", '@c', N'it''s @d' /* @e /* nested @f */ @g */ FROM T -- @h\nWHERE Id = @Id AND @@ROWCOUNT > 0")]
            public static partial class Q
            {
                public static partial Task<IReadOnlyList<long>> ExecuteAsync(IVelQuerySession session, long id);
            }
            """);

    [Fact]
    public void VQ1002_reports_unused_parameters_but_not_session_or_token()
        => DiagnosticVerifier.Verify(UserRow + """
            [VelQuerySql("users.by-id", "SELECT Id, Name, Email FROM dbo.Users WHERE Id = @Id")]
            public static partial class UsersById
            {
                public static partial Task<UserRow?> ExecuteAsync(IVelQuerySession session, long id, string {|VQ1002:name|}, CancellationToken cancellationToken);
            }
            """);

    [Fact]
    public void VQ1003_unsupported_row_types()
        => DiagnosticVerifier.Verify("""
            public interface IRow { long Id { get; } }
            public sealed class NoCtor { private NoCtor() { } public long Id { get; set; } }
            public sealed class Nested { public long Id { get; set; } public List<int> Items { get; set; } = new(); }
            public sealed class TwoCtors { public TwoCtors(long id) { } public TwoCtors(string name) { } }

            [VelQuerySql("a", "SELECT 1")]
            public static partial class A { public static partial {|VQ1003:Task<IRow>|} ExecuteAsync(IVelQuerySession session); }

            [VelQuerySql("b", "SELECT 1")]
            public static partial class B { public static partial {|VQ1003:Task<NoCtor>|} ExecuteAsync(IVelQuerySession session); }

            [VelQuerySql("c", "SELECT 1")]
            public static partial class C { public static partial {|VQ1003:Task<IReadOnlyList<Nested>>|} ExecuteAsync(IVelQuerySession session); }

            [VelQuerySql("d", "SELECT 1")]
            public static partial class D { public static partial {|VQ1003:IAsyncEnumerable<TwoCtors>|} ExecuteAsync(IVelQuerySession session); }

            [VelQuerySql("e", "SELECT 1", Cardinality = QueryCardinality.Scalar)]
            public static partial class E { public static partial {|VQ1015:Task<Nested>|} ExecuteAsync(IVelQuerySession session); }
            """);

    [Fact]
    public void NotMapped_members_are_ignored()
        => DiagnosticVerifier.Verify("""
            public sealed class Row { public long Id { get; set; } [NotMapped] public List<int> Items { get; set; } = new(); }

            [VelQuerySql("a", "SELECT Id FROM T")]
            public static partial class A { public static partial Task<IReadOnlyList<Row>> ExecuteAsync(IVelQuerySession session); }
            """);

    [Fact]
    public void VQ1004_two_members_mapping_to_one_column()
        => DiagnosticVerifier.Verify("""
            public sealed class Row
            {
                public long Id { get; set; }
                [Column("id")] public long Key { get; set; }
            }

            public sealed record Rec([property: Column("Name")] string Display, string Name);

            [VelQuerySql("a", "SELECT Id FROM T")]
            public static partial class A { public static partial {|VQ1004:Task<IReadOnlyList<Row>>|} ExecuteAsync(IVelQuerySession session); }

            [VelQuerySql("b", "SELECT Name FROM T")]
            public static partial class B { public static partial {|VQ1004:Task<Rec>|} ExecuteAsync(IVelQuerySession session); }
            """);

    [Fact]
    public void VQ1012_contract_type_must_be_partial()
        => DiagnosticVerifier.Verify("""
            [VelQuerySql("a", "SELECT 1")]
            public static class {|VQ1012:A|} { }

            public static class Outer
            {
                [VelQuerySql("b", "SELECT 1")]
                public static partial class {|VQ1012:B|} { public static partial Task<long> ExecuteAsync(IVelQuerySession session); }
            }

            [VelQuerySql("c", "SELECT 1")]
            public static partial class {|VQ1012:C|}<T> { public static partial Task<long> ExecuteAsync(IVelQuerySession session); }
            """);

    [Fact]
    public void VQ1013_contract_must_declare_exactly_one_partial_method()
        => DiagnosticVerifier.Verify("""
            [VelQuerySql("a", "SELECT 1")]
            public static partial class {|VQ1013:A|} { public static Task<long> ExecuteAsync(IVelQuerySession session) => Task.FromResult(1L); }

            [VelQuerySql("b", "SELECT 1")]
            public static partial class {|VQ1013:B|}
            {
                public static partial Task<long> OneAsync(IVelQuerySession session);
                public static partial Task<long> TwoAsync(IVelQuerySession session);
            }
            """);

    [Fact]
    public void VQ1014_invalid_method_signatures()
        => DiagnosticVerifier.Verify("""
            [VelQuerySql("a", "SELECT @Id")]
            public static partial class A { public static partial Task<long> ExecuteAsync(long id, IVelQuerySession {|VQ1014:session|}); }

            [VelQuerySql("b", "SELECT @Id")]
            public static partial class B { public static partial Task<long> ExecuteAsync(IVelQuerySession session, CancellationToken {|VQ1014:cancellationToken|}, long id); }

            [VelQuerySql("c", "SELECT @Id")]
            public static partial class C { public static partial Task<long> ExecuteAsync(IVelQuerySession session, ref long {|VQ1014:id|}); }

            [VelQuerySql("d", "SELECT 1")]
            public static partial class D { public static partial Task<long> {|VQ1014:ExecuteAsync|}<T>(IVelQuerySession session); }

            [VelQuerySql("e", "SELECT 1")]
            public static partial class E { public static partial Task<long> {|VQ1014:ExecuteAsync|}(); }
            """);

    [Fact]
    public void VQ1015_unsupported_return_type_and_cardinality_combinations()
        => DiagnosticVerifier.Verify("""
            [VelQuerySql("a", "SELECT 1", Cardinality = QueryCardinality.First)]
            public static partial class A { public static partial {|VQ1015:Task<IReadOnlyList<long>>|} ExecuteAsync(IVelQuerySession session); }

            [VelQuerySql("b", "SELECT 1")]
            public static partial class B { public static partial {|VQ1015:ValueTask<long>|} ExecuteAsync(IVelQuerySession session); }

            [VelQuerySql("c", "DELETE FROM T", Cardinality = QueryCardinality.NonQuery)]
            public static partial class C { public static partial {|VQ1015:Task<long>|} ExecuteAsync(IVelQuerySession session); }

            [VelQuerySql("d", "SELECT 1", Cardinality = QueryCardinality.FirstOrDefault)]
            public static partial class D { public static partial {|VQ1015:Task<long>|} ExecuteAsync(IVelQuerySession session); }

            [VelQuerySql("e", "SELECT 1", Cardinality = QueryCardinality.Single)]
            public static partial class E { public static partial {|VQ1015:IAsyncEnumerable<long>|} ExecuteAsync(IVelQuerySession session); }
            """);

    [Fact]
    public void Explicit_cardinalities_with_matching_return_types_are_valid()
        => DiagnosticVerifier.Verify("""
            [VelQuerySql("a", "SELECT 1", Cardinality = QueryCardinality.First)]
            public static partial class A { public static partial Task<long> ExecuteAsync(IVelQuerySession session); }

            [VelQuerySql("b", "SELECT 1", Cardinality = QueryCardinality.FirstOrDefault)]
            public static partial class B { public static partial Task<long?> ExecuteAsync(IVelQuerySession session); }

            [VelQuerySql("c", "SELECT 1", Cardinality = QueryCardinality.Scalar)]
            public static partial class C { public static partial Task<int> ExecuteAsync(IVelQuerySession session); }

            [VelQuerySql("d", "SELECT 'x'", Cardinality = QueryCardinality.SingleOrDefault)]
            public static partial class D { public static partial Task<string?> ExecuteAsync(IVelQuerySession session); }

            [VelQuerySql("e", "SELECT 1", Cardinality = QueryCardinality.Many)]
            public static partial class E { public static partial IAsyncEnumerable<long> ExecuteAsync(IVelQuerySession session); }
            """);

    [Fact]
    public void VQ1016_ambiguous_operations_require_explicit_cardinality()
        => DiagnosticVerifier.Verify(UserRow + """
            [VelQuerySql("a", "UPDATE T SET X = 1")]
            public static partial class A { public static partial {|VQ1016:Task<int>|} ExecuteAsync(IVelQuerySession session); }

            #nullable disable
            [VelQuerySql("b", "SELECT Id, Name, Email FROM T")]
            public static partial class B { public static partial {|VQ1016:Task<UserRow>|} ExecuteAsync(IVelQuerySession session); }
            #nullable restore
            """);

    [Fact]
    public void VQ1017_invalid_attribute_arguments()
        => DiagnosticVerifier.Verify("""
            [VelQuerySql({|VQ1017:""|}, {|VQ1017:" "|}, CommandTimeoutSeconds = {|VQ1017:-1|}, CommandType = {|VQ1017:CommandType.TableDirect|})]
            public static partial class A { public static partial Task<long> ExecuteAsync(IVelQuerySession session); }
            """);

    [Fact]
    public void VQ1018_parameter_without_inferable_db_type()
        => DiagnosticVerifier.Verify("""
            [VelQuerySql("a", "SELECT 1 WHERE @Ids IS NOT NULL AND @Raw IS NOT NULL")]
            public static partial class A
            {
                public static partial Task<long> ExecuteAsync(IVelQuerySession session, List<int> {|VQ1018:ids|}, [VelQueryParameter(DbType = DbType.Object)] object raw);
            }
            """);

    [Fact]
    public void VQ1019_unterminated_sql_constructs_warn()
        => DiagnosticVerifier.Verify("""
            [VelQuerySql("a", {|VQ1019:"SELECT 1 WHERE Name = 'abc"|})]
            public static partial class A { public static partial Task<long> ExecuteAsync(IVelQuerySession session); }
            """);

    [Fact]
    public void VQ1020_two_parameters_binding_one_placeholder()
        => DiagnosticVerifier.Verify("""
            [VelQuerySql("a", "SELECT 1 WHERE Id = @Id")]
            public static partial class A
            {
                public static partial Task<long> ExecuteAsync(IVelQuerySession session, long id, [VelQueryParameter(Name = "Id")] long {|VQ1020:other|});
            }
            """);

    [Theory]
    [InlineData("[VelQuerySql(\"a\")] public static partial class A { public static partial Task<long> ExecuteAsync(IVelQuerySession session); }")]
    [InlineData("[VelQuerySql(\"a\", \"SELECT @Id\")] public static partial class A { public static partial Task<Missing> ExecuteAsync(IVelQuerySession session, Unknown id); }")]
    [InlineData("[VelQuerySql(\"a\", \"SELECT @Id\")] public static partial class A { public static partial Task<long> ExecuteAsync(IVelQuerySession session, ")]
    [InlineData("[VelQuerySql(\"a\", null)] public static partial class A { public static partial Task<long> ExecuteAsync(IVelQuerySession session); }")]
    [InlineData("[VelQuerySql(\"a\", \"SELECT 1\", Cardinality = (QueryCardinality)42)] public static partial class A { public static partial Task<long> ExecuteAsync(IVelQuerySession session); }")]
    [InlineData("[VelQuerySql(\"a\", \"SELECT 1\")] public static partial class A { public static partial void Execute(IVelQuerySession session); }")]
    [InlineData("[VelQuerySql(\"a\", \"SELECT 1\")] public static partial class A { public static partial Task<List<> > ExecuteAsync(IVelQuerySession session); }")]
    public void Malformed_input_never_crashes_the_generator(string source)
    {
        var messages = DiagnosticVerifier.Messages(source); // throws if the generator throws

        Assert.DoesNotContain(messages, m => m.StartsWith("VQ1021", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Messages_name_query_parameter_and_method()
    {
        var messages = DiagnosticVerifier.Messages(UserRow + """
            [VelQuerySql("users.by-id", "SELECT Id FROM T WHERE Id = @Ident")]
            public static partial class UsersById
            {
                public static partial Task<UserRow?> ExecuteAsync(IVelQuerySession session, long id);
            }
            """);

        Assert.Contains("VQ1001: SQL of query 'users.by-id' references parameter '@Ident', which is not a parameter of 'ExecuteAsync'; add a typed parameter or fix the spelling", messages);
        Assert.Contains("VQ1002: Parameter 'id' of 'ExecuteAsync' is not referenced by the SQL of query 'users.by-id' and will not be bound; remove it or reference '@id'", messages);
    }
}
