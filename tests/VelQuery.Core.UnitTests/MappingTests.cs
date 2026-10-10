using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Core.UnitTests.Fakes;
using VelQuery.Mapping;
using Xunit;

namespace VelQuery.Core.UnitTests;

/// <summary>VQ-FR-014..017, VQ-FR-020 and PRD §9.3 mapping rules, via the opt-in reflection fallback and descriptors.</summary>
public sealed class MappingTests
{
    private const string Sql = "select * from people";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeResultSet People(params string[] columns)
    {
        var typed = new (string, Type)[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            typed[i] = (columns[i], columns[i].Contains("id", StringComparison.OrdinalIgnoreCase) ? typeof(int) : typeof(string));
        }

        return new FakeResultSet(typed);
    }

    private static async Task<IReadOnlyList<T>> QueryAsync<T>(FakeResultSet set, bool strict = true)
    {
        var h = Harness.WithFallback(strict);
        h.Script(Sql, set);
        await using var s = h.Session();
        try
        {
            return await s.Sql(Sql).WithQueryId("mapping.test").QueryAsync<T>(Ct);
        }
        finally
        {
            h.Db.AssertAllReleasedExactlyOnce();
        }
    }

    [Fact]
    public async Task Class_with_settable_properties_maps_by_name_case_insensitively()
    {
        var rows = await QueryAsync<PersonClass>(People("ID", "NAME", "email").Row(1, "Ann", "a@x").Row(2, "Bob", null));

        Assert.Collection(rows,
            r => Assert.Equal((1, "Ann", (string?)"a@x"), (r.Id, r.Name, r.Email)),
            r => Assert.Equal((2, "Bob", (string?)null), (r.Id, r.Name, r.Email)));
    }

    [Fact]
    public async Task Record_maps_through_its_constructor()
    {
        var rows = await QueryAsync<PersonRecord>(People("Id", "Name", "Email").Row(7, "Cy", null));
        Assert.Equal(new PersonRecord(7, "Cy", null), Assert.Single(rows));
    }

    [Fact]
    public async Task Descriptor_with_reflection_mapper_maps_records()
    {
        var h = new Harness();
        h.Script(Sql, People("Id", "Name", "Email").Row(1, "A", "e"));
        await using var s = h.Session();

        var row = await s.QuerySingleAsync(Descriptors.Reflection<PersonRecord>(Sql), new NoArgs(), Ct);

        Assert.Equal(new PersonRecord(1, "A", "e"), row);
    }

    [Fact]
    public async Task Extra_columns_are_ignored_even_when_duplicated()
    {
        var set = new FakeResultSet(("Id", typeof(int)), ("Name", typeof(string)), ("extra", typeof(int)), ("extra", typeof(int)))
            .Row(1, "A", 5, 6);
        var row = Assert.Single(await QueryAsync<PersonClass>(set));
        Assert.Equal(1, row.Id);
        Assert.Null(row.Email);
    }

    [Fact]
    public async Task Column_attribute_is_honored_for_properties_and_record_parameters()
    {
        var set = new FakeResultSet(("Id", typeof(int)), ("person_id", typeof(int)), ("full_name", typeof(string)), ("Name", typeof(string)))
            .Row(99, 1, "Full", "Short");

        var cls = Assert.Single(await QueryAsync<AliasedPerson>(set));
        Assert.Equal(1, cls.Id);
        Assert.Equal("Full", cls.Name);

        var rec = Assert.Single(await QueryAsync<AliasedRecord>(set));
        Assert.Equal(new AliasedRecord(1, "Short"), rec);
    }

    [Fact]
    public async Task Strict_null_into_non_nullable_value_type_names_column_and_type()
    {
        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() =>
            QueryAsync<PersonClass>(People("Id", "Name").Row(1, "A").Row(null, "B")));

        Assert.Equal("Id", ex.Column);
        Assert.Equal(typeof(int), ex.TargetType);
        Assert.Equal("mapping.test", ex.QueryId);
        Assert.Contains("'Id'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Int32", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Strict_null_into_non_nullable_reference_property_fails()
    {
        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => QueryAsync<PersonClass>(People("Id", "Name").Row(1, null)));
        Assert.Equal("Name", ex.Column);
        Assert.Equal(typeof(string), ex.TargetType);
    }

    [Fact]
    public async Task Strict_null_into_non_nullable_record_parameter_fails()
    {
        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => QueryAsync<PersonRecord>(People("Id", "Name", "Email").Row(1, null, null)));
        Assert.Equal("Name", ex.Column);
    }

    [Fact]
    public async Task Non_strict_mode_assigns_defaults_for_nulls()
    {
        var row = Assert.Single(await QueryAsync<PersonClass>(People("Id", "Name").Row(null, null), strict: false));
        Assert.Equal(0, row.Id);
        Assert.Null(row.Name);
    }

    [Fact]
    public async Task Missing_constructor_column_fails_before_any_row_is_returned()
    {
        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => QueryAsync<PersonRecord>(People("Id", "Email").Row(1, null)));
        Assert.Equal("Name", ex.Column);
        Assert.Equal(typeof(string), ex.TargetType);
    }

    [Fact]
    public async Task Missing_required_property_column_fails()
    {
        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => QueryAsync<RequiredPerson>(People("Note").Row("n")));
        Assert.Equal("Id", ex.Column);

        var row = Assert.Single(await QueryAsync<RequiredPerson>(People("Id").Row(3)));
        Assert.Equal(3, row.Id);
        Assert.Null(row.Note);
    }

    [Fact]
    public async Task Missing_optional_property_column_keeps_the_default()
    {
        var row = Assert.Single(await QueryAsync<PersonClass>(People("Id", "Name").Row(1, "A")));
        Assert.Null(row.Email);
    }

    [Theory]
    [InlineData("Id", "Id")]
    [InlineData("Id", "ID")]
    [InlineData("person_id", "PERSON_ID")]
    public async Task Duplicate_columns_matching_a_member_are_ambiguous(string first, string second)
    {
        var set = new FakeResultSet((first, typeof(int)), (second, typeof(int)), ("Name", typeof(string))).Row(1, 2, "A");

        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() =>
            first == "Id" ? QueryAsync<PersonClass>(set) : QueryAsync<AliasedPerson>(set));

        Assert.Contains("more than one column", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enums_map_from_integral_columns_including_nullable_enums()
    {
        var set = new FakeResultSet(("Id", typeof(int)), ("Status", typeof(int)), ("Previous", typeof(byte)))
            .Row(1, 2, (byte)1)
            .Row(2, 1, null);

        var rows = await QueryAsync<StatusRow>(set);

        Assert.Equal([Status.Disabled, Status.Active], [rows[0].Status, rows[1].Status]);
        Assert.Equal(Status.Active, rows[0].Previous);
        Assert.Null(rows[1].Previous);
    }

    [Fact]
    public async Task Lossy_property_conversion_is_rejected_with_column_and_types()
    {
        var set = new FakeResultSet(("Id", typeof(int)), ("Count", typeof(string))).Row(1, "12");

        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => QueryAsync<LooseRow>(set));

        Assert.Equal("Count", ex.Column);
        Assert.Contains("String", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Int32", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Widening_property_conversion_is_accepted()
    {
        var set = new FakeResultSet(("Id", typeof(short)), ("Count", typeof(byte))).Row((short)4, (byte)9);
        var row = Assert.Single(await QueryAsync<LooseRow>(set));
        Assert.Equal((4, 9), (row.Id, row.Count));
    }

    [Fact]
    public async Task No_registered_mapper_and_fallback_disabled_throws_unsupported_capability_without_io()
    {
        var h = new Harness();
        h.Script(Sql, People("Id").Row(1));
        await using var s = h.Session();

        var ex = await Assert.ThrowsAsync<VelQueryUnsupportedCapabilityException>(() => s.QueryAsync<UnregisteredRow>(Sql, null, Ct));
        Assert.Equal(VelQueryErrorCategory.UnsupportedCapability, ex.Category);
        Assert.Contains(nameof(UnregisteredRow), ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<VelQueryUnsupportedCapabilityException>(() => s.QueryFirstAsync<UnregisteredRow>(Sql, null, Ct));
        await Assert.ThrowsAsync<VelQueryUnsupportedCapabilityException>(() => s.QuerySingleOrDefaultAsync<UnregisteredRow>(Sql, null, Ct));
        Assert.Throws<VelQueryUnsupportedCapabilityException>(() => s.StreamAsync<UnregisteredRow>(Sql, null, Ct));

        Assert.Empty(h.Db.Connections);

        await using (var multi = await s.QueryMultipleAsync(Sql, null, Ct))
        {
            await Assert.ThrowsAsync<VelQueryUnsupportedCapabilityException>(() => multi.ReadAsync<UnregisteredRow>(Ct));
        }

        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Registered_mapper_is_used_when_fallback_is_disabled()
    {
        RowMappers<RegisteredRow>.Register(new RegisteredRowFactory());
        var h = new Harness();
        h.Script(Sql, People("Id").Row(5));
        await using var s = h.Session();

        var row = await s.QuerySingleAsync<RegisteredRow>(Sql, null, Ct);

        Assert.Equal(50, row.Value);
    }

    [Fact]
    public async Task Type_without_a_usable_constructor_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => QueryAsync<NoPublicConstructor>(People("Id").Row(1)));
        Assert.Equal(typeof(NoPublicConstructor), ex.TargetType);
    }

    [Fact]
    public async Task Scalar_column_null_in_strict_mode_names_the_column()
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Column<int>("total", 1, null));
        await using var s = h.Session();

        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => s.QueryAsync<int>(Sql, null, Ct));

        Assert.Equal("total", ex.Column);
        Assert.Equal(typeof(int), ex.TargetType);
    }

    [Fact]
    public async Task Scalar_mapper_rejects_result_without_columns()
    {
        var h = new Harness();
        h.Db.Script(Sql, new FakeScript().With(new FakeResultSet()));
        await using var s = h.Session();

        await Assert.ThrowsAsync<VelQueryMappingException>(() => s.QueryAsync<int>(Sql, null, Ct));
        h.Db.AssertAllReleasedExactlyOnce();
    }
}

public sealed class RegisteredRow
{
    public int Value { get; init; }
}

public sealed class NoPublicConstructor
{
    private NoPublicConstructor()
    {
    }

    public int Id { get; set; }

    public static NoPublicConstructor Create() => new();
}

internal sealed class RegisteredRowFactory : IRowMapperFactory<RegisteredRow>
{
    public IRowMapper<RegisteredRow> Create(DbDataReader reader, in MappingContext context) => new Mapper(reader.GetOrdinal("Id"));

    private sealed class Mapper(int ordinal) : IRowMapper<RegisteredRow>
    {
        public RegisteredRow Map(DbDataReader reader) => new() { Value = reader.GetInt32(ordinal) * 10 };
    }
}
