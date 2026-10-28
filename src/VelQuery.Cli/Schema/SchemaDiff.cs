using System;
using System.Collections.Generic;
using System.Linq;

namespace VelQuery.Cli.Schema;

/// <summary>
/// One difference between an approved snapshot and the current database. <see cref="Breaking"/> means code written
/// against the approved snapshot can now fail.
/// </summary>
internal sealed record SchemaChange(bool Breaking, string Kind, string Target, string? Detail);

/// <summary>
/// Compares two snapshots and classifies each change (VQ-FR-039). A change is breaking when queries checked against
/// the old snapshot can fail against the new schema: a removed table, view or column; a changed type; a column that
/// became nullable (non-nullable members now see NULL); or a column that became required for INSERT.
/// </summary>
internal static class SchemaDiff
{
    public static List<SchemaChange> Compare(DbSchema approved, DbSchema current)
    {
        var changes = new List<SchemaChange>();
        if (!string.Equals(approved.Dialect, current.Dialect, StringComparison.Ordinal))
        {
            changes.Add(new SchemaChange(true, "dialect changed", "database", $"{approved.Dialect} -> {current.Dialect}"));
            return changes;
        }

        var comparer = approved.CaseSensitiveTables ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var columnComparer = approved.CaseSensitiveColumns ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var now = current.Objects.ToDictionary(o => Key(o), o => o, comparer);
        var before = approved.Objects.ToDictionary(o => Key(o), o => o, comparer);

        foreach (var old in approved.Objects.OrderBy(o => o.Display, StringComparer.Ordinal))
        {
            if (!now.TryGetValue(Key(old), out var updated))
            {
                changes.Add(new SchemaChange(true, old.Kind + " removed", old.Display, null));
                continue;
            }

            if (!string.Equals(old.Kind, updated.Kind, StringComparison.Ordinal))
            {
                changes.Add(new SchemaChange(false, "kind changed", old.Display, $"{old.Kind} -> {updated.Kind}"));
            }

            var columns = updated.Columns.ToDictionary(c => c.Name, c => c, columnComparer);
            foreach (var column in old.Columns)
            {
                var target = old.Display + "." + column.Name;
                if (!columns.TryGetValue(column.Name, out var next))
                {
                    changes.Add(new SchemaChange(true, "column removed", target, null));
                    continue;
                }

                if (!string.Equals(column.DbType, next.DbType, StringComparison.OrdinalIgnoreCase))
                {
                    changes.Add(new SchemaChange(true, "type changed", target, $"{column.DbType} -> {next.DbType}"));
                }
                else if (column.MaxLength != next.MaxLength || column.Precision != next.Precision || column.Scale != next.Scale)
                {
                    // Shrinking can truncate or reject values that were valid before; growing is safe.
                    var shrank = Shrank(column.MaxLength, next.MaxLength) || Shrank(column.Precision, next.Precision) || Shrank(column.Scale, next.Scale);
                    changes.Add(new SchemaChange(shrank, "size changed", target, $"{Size(column)} -> {Size(next)}"));
                }

                if (column.Nullable == false && next.Nullable == true)
                {
                    changes.Add(new SchemaChange(true, "became nullable", target, null));
                }
                else if (column.Nullable == true && next.Nullable == false)
                {
                    changes.Add(new SchemaChange(!next.HasDefault, "became NOT NULL", target, next.HasDefault ? "has a default" : "inserts must now set it"));
                }

                if (column.HasDefault && !next.HasDefault && next.Nullable == false)
                {
                    changes.Add(new SchemaChange(true, "default removed", target, "inserts must now set it"));
                }

                if (column.Generated != next.Generated)
                {
                    changes.Add(new SchemaChange(next.Generated, next.Generated ? "became generated" : "no longer generated", target, null));
                }
            }

            var oldNames = new HashSet<string>(old.Columns.Select(c => c.Name), columnComparer);
            foreach (var added in updated.Columns.Where(c => !oldNames.Contains(c.Name)))
            {
                var required = added.Nullable == false && !added.HasDefault && !added.Generated;
                changes.Add(new SchemaChange(required, "column added", old.Display + "." + added.Name, required ? "NOT NULL without default: inserts must set it" : null));
            }
        }

        foreach (var added in current.Objects.Where(o => !before.ContainsKey(Key(o))).OrderBy(o => o.Display, StringComparer.Ordinal))
        {
            changes.Add(new SchemaChange(false, added.Kind + " added", added.Display, null));
        }

        return changes;
    }

    /// <summary>
    /// Whether a size limit got tighter. No limit (nvarchar(max), text, unconstrained numeric) becoming a limit is a
    /// shrink too; lifted <c>int?</c> comparisons would call that "not smaller".
    /// </summary>
    private static bool Shrank(int? before, int? after) => after is { } now && (before is not { } then || now < then);

    private static string Key(DbObject obj) => (obj.Schema ?? string.Empty) + "\u0001" + obj.Name;

    private static string Size(DbColumn column)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (column.MaxLength is { } length)
        {
            return length.ToString(invariant);
        }

        return column.Precision is null && column.Scale is null
            ? "unbounded"
            : $"{column.Precision?.ToString(invariant) ?? "?"},{column.Scale?.ToString(invariant) ?? "?"}";
    }
}
