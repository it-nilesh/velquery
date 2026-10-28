using System;
using System.Collections.Generic;
using System.Linq;
using VelQuery.Generators.Diagnostics;
using VelQuery.Generators.Infrastructure;
using VelQuery.Generators.Sql;

namespace VelQuery.Generators.Schema;

/// <summary>
/// The snapshot plus name lookups, as one incremental pipeline value. Equality is the snapshot's, so contracts are
/// re-checked only when the snapshot content changes.
/// </summary>
internal sealed class SchemaCatalog : IEquatable<SchemaCatalog>
{
    private readonly Dictionary<string, List<SchemaObject>> _byName;

    public SchemaCatalog(SchemaSnapshot snapshot)
    {
        Snapshot = snapshot;
        _byName = new Dictionary<string, List<SchemaObject>>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in snapshot.Objects)
        {
            if (!_byName.TryGetValue(obj.Name, out var list))
            {
                _byName[obj.Name] = list = [];
            }

            list.Add(obj);
        }
    }

    public SchemaSnapshot Snapshot { get; }

    public SqlDialect Dialect => Snapshot.Dialect;

    public IEnumerable<string> ObjectNames => Snapshot.Objects.Select(o => o.Name);

    /// <summary>Objects whose name matches (case per snapshot), optionally in a given schema.</summary>
    public IEnumerable<SchemaObject> Find(SqlName name, SqlName? schema)
    {
        if (!_byName.TryGetValue(name.Text, out var candidates))
        {
            return [];
        }

        return candidates.Where(o => SameTableName(name, o.Name) && (schema is null || (o.Schema is not null && SameTableName(schema, o.Schema))));
    }

    /// <summary>Compares an identifier as written with a catalog name, applying PostgreSQL case folding.</summary>
    public bool SameTableName(SqlName written, string catalog) => Same(written, catalog, Snapshot.CaseSensitiveTables);

    public bool SameColumnName(SqlName written, string catalog) => Same(written, catalog, Snapshot.CaseSensitiveColumns);

    public string Fold(SqlName name) => Dialect == SqlDialect.PostgreSql && !name.Quoted ? name.Text.ToLowerInvariant() : name.Text;

    private bool Same(SqlName written, string catalog, bool caseSensitive)
        => string.Equals(Fold(written), catalog, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    public bool Equals(SchemaCatalog? other) => other is not null && Snapshot.Equals(other.Snapshot);

    public override bool Equals(object? obj) => Equals(obj as SchemaCatalog);

    public override int GetHashCode() => Snapshot.GetHashCode();
}

/// <summary>
/// Checks one contract's <see cref="QueryShape"/> against the schema snapshot (ADR-0006, VQ-FR-037): referenced
/// tables and columns exist (VQ1005), the result supplies what the row type needs with compatible types and
/// nullability (VQ1025–VQ1028), and writes respect required and generated columns (VQ1029, VQ1030). Whatever cannot
/// be resolved with certainty is skipped, never reported.
/// </summary>
internal sealed class SchemaChecker
{
    private readonly SchemaCheckInput _input;
    private readonly QueryShape _shape;
    private readonly SchemaCatalog _catalog;
    private readonly List<DiagnosticInfo> _diagnostics = [];
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly TableResolution[] _tables;
    private readonly Dictionary<int, List<OutputInfo>?> _outputs = [];
    private readonly HashSet<int> _resolving = [];

    private SchemaChecker(SchemaCheckInput input, QueryShape shape, SchemaCatalog catalog)
    {
        _input = input;
        _shape = shape;
        _catalog = catalog;
        _tables = shape.Tables.Select(ResolveTable).ToArray();
    }

    public static EquatableArray<DiagnosticInfo> Check(SchemaCheckInput input, SchemaCatalog? catalog)
    {
        if (catalog is null)
        {
            return EquatableArray<DiagnosticInfo>.Empty;
        }

        if (input.Dialect != catalog.Dialect)
        {
            return Note(input, $"the schema snapshot is for {SqlDialects.DisplayName(catalog.Dialect)}, but the query uses the {SqlDialects.DisplayName(input.Dialect)} dialect");
        }

        if (input.Shape is null)
        {
            return input.SkipReason is null ? EquatableArray<DiagnosticInfo>.Empty : Note(input, input.SkipReason);
        }

        var checker = new SchemaChecker(input, input.Shape, catalog);
        checker.Run();
        return checker._diagnostics.ToEquatableArray();
    }

    private static EquatableArray<DiagnosticInfo> Note(SchemaCheckInput input, string reason)
        => new[] { DiagnosticInfo.Create(Descriptors.NotSchemaChecked, input.SqlLocation, input.QueryId, reason) }.ToEquatableArray();

    private void Run()
    {
        var notes = new List<string>(_shape.Unsupported);
        for (var i = 0; i < _shape.Tables.Count; i++)
        {
            var table = _shape.Tables[i];
            if (_tables[i].Status == Status.Missing)
            {
                Report(Descriptors.NotInSnapshot, table.Location, $"table or view '{table.Display}'", Suggest(table.Name.Text, _catalog.ObjectNames));
            }
        }

        for (var s = 0; s < _shape.Scopes.Count; s++)
        {
            foreach (var reference in _shape.Scopes[s].References)
            {
                CheckReference(s, reference);
            }
        }

        foreach (var insert in _shape.Inserts)
        {
            CheckInsert(insert);
        }

        if (_input.ReturnsRows && _shape.Result >= 0 && _input.Members.Count > 0)
        {
            var outputs = Outputs(_shape.Result);
            if (outputs is null)
            {
                if (!_input.Scalar)
                {
                    notes.Add("its result columns could not be determined");
                }
            }
            else
            {
                CheckResult(outputs);
            }
        }

        if (notes.Count > 0)
        {
            _diagnostics.Add(DiagnosticInfo.Create(Descriptors.NotSchemaChecked, _input.SqlLocation, _input.QueryId, string.Join("; ", notes)));
        }
    }

    // ------------------------------------------------------------------ references

    private void CheckReference(int scope, ColumnReference reference)
    {
        var (status, column, searched, candidates) = Resolve(scope, reference);
        if (status == Status.Missing)
        {
            var where = searched.Count == 0 ? string.Empty : $" in {string.Join(", ", searched.Select(n => "'" + n + "'"))}";
            Report(Descriptors.NotInSnapshot, reference.Location, $"column '{reference.Display}'{where}", Suggest(reference.Column.Text, candidates));
        }
        else if (status == Status.Found && reference.Write && column!.Generated)
        {
            Report(Descriptors.WritesGeneratedColumn, reference.Location, column.Name, column.Origin);
        }
    }

    private (Status Status, ColumnInfo? Column, List<string> Searched, List<string> Candidates) Resolve(int scope, ColumnReference reference)
    {
        var searched = new List<string>();
        var candidates = new List<string>();
        var parts = reference.Parts;
        var unknown = (Status.Unknown, (ColumnInfo?)null, searched, candidates);
        var name = reference.Column;
        if (parts.Count == 1)
        {
            if (reference.Target >= 0)
            {
                var columns = SourceColumns(scope, reference.Target);
                if (columns is null)
                {
                    return unknown;
                }

                var target = _shape.Scopes[scope].Sources[reference.Target];
                searched.Add(SourceDisplay(target));
                candidates.AddRange(columns.Select(c => c.Name));
                var match = columns.FirstOrDefault(c => _catalog.SameColumnName(name, c.Name));
                return match is null ? (Status.Missing, null, searched, candidates) : (Status.Found, match, searched, candidates);
            }

            var anyUnknown = false;
            var anySource = false;
            for (var s = scope; s >= 0; s = _shape.Scopes[s].Parent)
            {
                var block = _shape.Scopes[s];
                if (block.Opaque)
                {
                    return unknown;
                }

                for (var k = 0; k < block.Sources.Count; k++)
                {
                    var source = block.Sources[k];
                    anySource = true;
                    var columns = SourceColumns(s, k);
                    if (columns is null)
                    {
                        anyUnknown = true;
                        continue;
                    }

                    var match = columns.FirstOrDefault(c => _catalog.SameColumnName(name, c.Name));
                    if (match is not null)
                    {
                        return (Status.Found, match, searched, candidates);
                    }

                    if (!source.Pseudo)
                    {
                        if (s == scope)
                        {
                            searched.Add(SourceDisplay(source));
                        }

                        candidates.AddRange(columns.Select(c => c.Name));
                    }
                }

                // A whole-row reference (PostgreSQL SELECT t FROM t) or a select-list alias (ORDER BY total).
                if (block.Sources.Any(src => src.Alias is { } alias && _catalog.SameTableName(name, _catalog.Fold(alias))) ||
                    block.Outputs.Any(o => IsAlias(o) && string.Equals(_catalog.Fold(o.Name!), _catalog.Fold(name), StringComparison.OrdinalIgnoreCase)))
                {
                    return unknown;
                }
            }

            return anyUnknown || !anySource ? unknown : (Status.Missing, null, searched, candidates);
        }

        if (parts.Count == 2)
        {
            var qualifier = parts[0];
            for (var s = scope; s >= 0; s = _shape.Scopes[s].Parent)
            {
                var block = _shape.Scopes[s];
                for (var k = 0; k < block.Sources.Count; k++)
                {
                    var source = block.Sources[k];
                    if (source.Alias is not { } alias || !_catalog.SameTableName(qualifier, _catalog.Fold(alias)))
                    {
                        continue;
                    }

                    var columns = SourceColumns(s, k);
                    if (columns is null)
                    {
                        return unknown;
                    }

                    var match = columns.FirstOrDefault(c => _catalog.SameColumnName(name, c.Name));
                    searched.Add(SourceDisplay(source));
                    candidates.AddRange(columns.Select(c => c.Name));
                    return match is null ? (Status.Missing, null, searched, candidates) : (Status.Found, match, searched, candidates);
                }
            }

            return unknown; // Not a source of this query: a schema-qualified function, pseudo-table or outer name.
        }

        if (parts.Count == 3)
        {
            for (var s = scope; s >= 0; s = _shape.Scopes[s].Parent)
            {
                var block = _shape.Scopes[s];
                for (var k = 0; k < block.Sources.Count; k++)
                {
                    var source = block.Sources[k];
                    if (source.Kind != SourceKind.Table || _shape.Tables[source.Table] is not { Schema: { } schema } table ||
                        !_catalog.SameTableName(parts[0], _catalog.Fold(schema)) || !_catalog.SameTableName(parts[1], _catalog.Fold(table.Name)))
                    {
                        continue;
                    }

                    var columns = SourceColumns(s, k);
                    if (columns is null)
                    {
                        return unknown;
                    }

                    var match = columns.FirstOrDefault(c => _catalog.SameColumnName(name, c.Name));
                    searched.Add(table.Display);
                    candidates.AddRange(columns.Select(c => c.Name));
                    return match is null ? (Status.Missing, null, searched, candidates) : (Status.Found, match, searched, candidates);
                }
            }
        }

        return unknown;
    }

    /// <summary>An output name introduced by an alias (not a plain column's own name), usable in ORDER BY and similar.</summary>
    private static bool IsAlias(QueryOutput output)
        => output.Name is not null && (output.Kind == OutputKind.Expression || (output.Kind == OutputKind.Column && output.Name != output.Reference!.Column));

    private string SourceDisplay(QuerySource source)
        => source.Kind == SourceKind.Table && _tables[source.Table].Object is { } obj ? obj.Display : source.Alias?.Text ?? "?";

    /// <summary>Columns of a source, or null when they cannot be known.</summary>
    private List<ColumnInfo>? SourceColumns(int scope, int index)
    {
        var source = _shape.Scopes[scope].Sources[index];
        List<ColumnInfo>? columns;
        switch (source.Kind)
        {
            case SourceKind.Table:
                var obj = _tables[source.Table].Object;
                if (obj is null)
                {
                    return null;
                }

                columns = obj.Columns.Select(c => new ColumnInfo(c.Name, c.DbType, c.Nullable, c.HasDefault, c.Generated, obj.Display)).ToList();
                break;
            case SourceKind.Derived:
                var outputs = Outputs(source.Scope);
                if (outputs is null || outputs.Any(o => o.Name is null))
                {
                    return null;
                }

                columns = outputs.Select(o => new ColumnInfo(o.Name!, o.DbType, o.Nullable, false, false, source.Alias?.Text ?? "?")).ToList();
                break;
            default:
                return null;
        }

        if (source.Renames.Count > 0)
        {
            if (source.Renames.Count > columns.Count)
            {
                return null;
            }

            for (var i = 0; i < source.Renames.Count; i++)
            {
                columns[i] = columns[i] with { Name = _catalog.Fold(source.Renames[i]) };
            }
        }

        if (source.Nullable)
        {
            columns = columns.Select(c => c with { Nullable = true }).ToList();
        }

        return columns;
    }

    /// <summary>Result columns of a block, or null when they cannot be determined.</summary>
    private List<OutputInfo>? Outputs(int scope)
    {
        if (_outputs.TryGetValue(scope, out var cached))
        {
            return cached;
        }

        if (!_resolving.Add(scope))
        {
            return null;
        }

        var result = ComputeOutputs(scope);
        _resolving.Remove(scope);
        _outputs[scope] = result;
        return result;
    }

    private List<OutputInfo>? ComputeOutputs(int scope)
    {
        var block = _shape.Scopes[scope];
        if (block.Members.Count > 0)
        {
            // Set operation: names come from the first block; types and nullability may differ per block.
            var first = Outputs(block.Members[0]);
            return first?.Select(o => o with { DbType = null, Nullable = null }).ToList();
        }

        if (!block.OutputsKnown || block.Opaque)
        {
            return null;
        }

        var outputs = new List<OutputInfo>();
        foreach (var output in block.Outputs)
        {
            switch (output.Kind)
            {
                case OutputKind.Star:
                    for (var k = 0; k < block.Sources.Count; k++)
                    {
                        if (block.Sources[k].Pseudo)
                        {
                            continue;
                        }

                        var columns = SourceColumns(scope, k);
                        if (columns is null)
                        {
                            return null;
                        }

                        outputs.AddRange(columns.Select(c => new OutputInfo(c.Name, c.DbType, c.Nullable, c.Name)));
                    }

                    break;
                case OutputKind.QualifiedStar:
                    var qualifier = output.Qualifier[output.Qualifier.Count - 1];
                    var index = -1;
                    for (var k = 0; k < block.Sources.Count; k++)
                    {
                        if (block.Sources[k].Alias is { } alias && _catalog.SameTableName(qualifier, _catalog.Fold(alias)))
                        {
                            index = k;
                            break;
                        }
                    }

                    var qualified = index < 0 ? null : SourceColumns(scope, index);
                    if (qualified is null)
                    {
                        return null;
                    }

                    outputs.AddRange(qualified.Select(c => new OutputInfo(c.Name, c.DbType, c.Nullable, c.Name)));
                    break;
                case OutputKind.Column:
                    var (status, column, _, _) = Resolve(scope, output.Reference!);
                    var outputName = _catalog.Fold(output.Name!);
                    if (status == Status.Found && output.Name == output.Reference!.Column)
                    {
                        outputName = column!.Name; // Unaliased: the database reports the catalog spelling.
                    }

                    outputs.Add(status == Status.Found
                        ? new OutputInfo(outputName, column!.DbType, column.Nullable, output.Reference!.Display)
                        : new OutputInfo(outputName, null, null, output.Reference!.Display, Broken: status == Status.Missing));
                    break;
                default:
                    outputs.Add(new OutputInfo(output.Name is null ? null : _catalog.Fold(output.Name), null, null, output.Name?.Text ?? "expression"));
                    break;
            }
        }

        return outputs;
    }

    // ------------------------------------------------------------------ results and writes

    private void CheckResult(List<OutputInfo> outputs)
    {
        if (_input.Scalar)
        {
            if (outputs.Count == 0)
            {
                return;
            }

            CheckValue(outputs[0], _input.Members[0], "the scalar result");
            return;
        }

        // An unnamed expression may supply any member; a column already reported as unknown (VQ1005) would only
        // repeat that error as a missing member, so both suppress VQ1025 until the SQL is fixed.
        var unnamed = outputs.Any(o => o.Name is null || o.Broken);
        foreach (var member in _input.Members)
        {
            var matches = outputs.Where(o => o.Name is not null && string.Equals(o.Name, member.Column, StringComparison.OrdinalIgnoreCase)).ToList();
            var display = ShortName(_input.RowType) + "." + member.Member;
            if (matches.Count == 0)
            {
                if (member.Required && !unnamed)
                {
                    var suggestion = Suggest(member.Column, outputs.Select(o => o.Name!));
                    var detail = suggestion.Length > 0 ? suggestion : "; it returns " + string.Join(", ", outputs.Select(o => o.Name));
                    Report(Descriptors.MissingResultColumn, _input.SqlLocation, member.Column, display, detail);
                }
            }
            else if (matches.Count > 1)
            {
                Report(Descriptors.DuplicateResultColumn, _input.SqlLocation, member.Column, display);
            }
            else
            {
                CheckValue(matches[0], member, "'" + display + "'");
            }
        }
    }

    private void CheckValue(OutputInfo output, ResultMember member, string display)
    {
        if (output.Nullable == true && !member.IsNullable)
        {
            Report(Descriptors.NullableToNonNullable, _input.SqlLocation, output.Origin, display);
        }

        if (output.DbType is { } dbType && !DbTypeRules.CanConvert(DbTypeRules.Classify(_input.Dialect, dbType), member.Kind))
        {
            Report(Descriptors.IncompatibleColumnType, _input.SqlLocation, output.Origin, dbType, ShortName(member.Type), display.Trim('\''));
        }
    }

    private void CheckInsert(InsertTarget insert)
    {
        var source = _shape.Scopes[insert.Scope].Sources[insert.Source];
        if (source.Kind != SourceKind.Table || _tables[source.Table].Object is not { } obj)
        {
            return;
        }

        foreach (var column in obj.Columns)
        {
            if (column.Nullable != false || column.HasDefault || column.Generated)
            {
                continue;
            }

            if (insert.AllDefaults || !insert.Columns.Any(c => _catalog.SameColumnName(c, column.Name)))
            {
                Report(Descriptors.InsertMissesRequiredColumn, _shape.Tables[source.Table].Location ?? _input.SqlLocation, obj.Display, column.Name);
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private TableResolution ResolveTable(TableReference table)
    {
        if (table.Name.Text.Length == 0 || table.Database is not null || IsSystem(table))
        {
            return new TableResolution(Status.Unknown, null);
        }

        // SQLite's main database has no schema name in the snapshot.
        var schemaName = _input.Dialect == SqlDialect.Sqlite && table.Schema is { } main && main.Text.Equals("main", StringComparison.OrdinalIgnoreCase) ? null : table.Schema;
        var matches = _catalog.Find(table.Name, schemaName).ToList();
        if (matches.Count == 0)
        {
            return new TableResolution(Status.Missing, null);
        }

        // Unqualified names resolve through the default schema first (dbo, public, the MySQL database).
        var preferred = table.Schema is null && _catalog.Snapshot.DefaultSchema is { } schema
            ? matches.FirstOrDefault(m => string.Equals(m.Schema, schema, StringComparison.OrdinalIgnoreCase)) ?? matches[0]
            : matches[0];
        return new TableResolution(Status.Found, preferred);
    }

    private bool IsSystem(TableReference table)
    {
        var schema = table.Schema is null ? null : _catalog.Fold(table.Schema).ToLowerInvariant();
        var name = _catalog.Fold(table.Name).ToLowerInvariant();
        if (schema is "sys" or "information_schema" or "pg_catalog" or "performance_schema" or "mysql" or "temp" && !_catalog.Find(table.Name, table.Schema).Any())
        {
            return true;
        }

        return _input.Dialect switch
        {
            SqlDialect.PostgreSql => schema is null && name.StartsWith("pg_", StringComparison.Ordinal),
            SqlDialect.Sqlite => name.StartsWith("sqlite_", StringComparison.Ordinal) || name.StartsWith("pragma_", StringComparison.Ordinal),
            SqlDialect.SqlServer => schema is null && name.StartsWith("sys", StringComparison.Ordinal) && !_catalog.Find(table.Name, null).Any(),
            _ => false,
        };
    }

    private void Report(Microsoft.CodeAnalysis.DiagnosticDescriptor descriptor, LocationInfo? location, params string[] arguments)
    {
        var all = new string[arguments.Length + 1];
        all[0] = _input.QueryId;
        Array.Copy(arguments, 0, all, 1, arguments.Length);
        var key = descriptor.Id + "|" + location?.Span.ToString() + "|" + string.Join("|", all);
        if (_reported.Add(key))
        {
            _diagnostics.Add(DiagnosticInfo.Create(descriptor, location ?? _input.SqlLocation, all));
        }
    }

    private static string ShortName(string? type) => type is null ? string.Empty : type.Replace("global::", string.Empty);

    /// <summary>"; did you mean 'X'?" for the closest candidate within a small edit distance, or empty.</summary>
    private static string Suggest(string name, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        var limit = Math.Max(1, Math.Min(3, name.Length / 3));
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.Ordinal))
        {
            var distance = Distance(name.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance <= limit && distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best is null ? string.Empty : $"; did you mean '{best}'?";
    }

    /// <summary>Edit distance where swapping two adjacent letters counts as one edit ("Usres" → "Users").</summary>
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }

    private enum Status
    {
        Unknown,
        Found,
        Missing,
    }

    private sealed record TableResolution(Status Status, SchemaObject? Object);

    /// <summary>A resolved column; <see cref="Origin"/> names its table or derived source for messages.</summary>
    private sealed record ColumnInfo(string Name, string? DbType, bool? Nullable, bool HasDefault, bool Generated, string Origin);

    /// <summary>A result column. <see cref="Name"/> is null for an unnamed expression; <see cref="Broken"/> marks an unknown column.</summary>
    private sealed record OutputInfo(string? Name, string? DbType, bool? Nullable, string Origin, bool Broken = false);
}
