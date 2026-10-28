using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VelQuery.Cli.Schema;

/// <summary>A column as exported to a snapshot. <see cref="Nullable"/> is null when the database does not report it.</summary>
internal sealed record DbColumn(string Name, string DbType, int? MaxLength, int? Precision, int? Scale, bool? Nullable, bool HasDefault, bool Generated);

/// <summary>A table or view as exported to a snapshot.</summary>
internal sealed record DbObject(string? Schema, string Name, string Kind, IReadOnlyList<DbColumn> Columns)
{
    public string Display => Schema is null ? Name : Schema + "." + Name;
}

/// <summary>A stored procedure or function parameter; <see cref="Mode"/> is <c>in</c>, <c>out</c> or <c>inout</c>.</summary>
internal sealed record DbParameter(string Name, string DbType, string Mode);

/// <summary>A stored procedure or function as exported to a snapshot.</summary>
internal sealed record DbRoutine(string? Schema, string Name, string Kind, IReadOnlyList<DbParameter> Parameters)
{
    public string Display => Schema is null ? Name : Schema + "." + Name;
}

/// <summary>
/// Database metadata for a <c>velquery.schema.v1</c> snapshot (PRD §11.2, ADR-0006): names, types, nullability and
/// defaults only. Never data rows, credentials, grants or comments.
/// </summary>
internal sealed record DbSchema(
    string Dialect,
    string? LogicalName,
    string? DefaultSchema,
    bool CaseSensitiveTables,
    bool CaseSensitiveColumns,
    IReadOnlyList<DbObject> Objects,
    IReadOnlyList<DbRoutine>? Routines = null)
{
    public const string Format = "velquery.schema.v1";

    /// <summary>
    /// Canonical serialization: objects ordered by schema and name (ordinal), columns in table order, fixed property
    /// order, LF line endings, no timestamps (VQ-FR-032), so the same database always produces the same bytes and a
    /// reviewed diff shows only real changes. <c>snapshotSha256</c> is the SHA-256 of this text without that line.
    /// </summary>
    public string ToCanonicalJson()
    {
        var body = Serialize(null);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        return Serialize(hash);
    }

    private string Serialize(string? hash)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        Property(sb, 1, "format", Format);
        Property(sb, 1, "dialect", Dialect);
        if (LogicalName is not null)
        {
            Property(sb, 1, "databaseLogicalName", LogicalName);
        }

        if (DefaultSchema is not null)
        {
            Property(sb, 1, "defaultSchema", DefaultSchema);
        }

        sb.Append("  \"caseSensitiveTables\": ").Append(Bool(CaseSensitiveTables)).Append(",\n");
        sb.Append("  \"caseSensitiveColumns\": ").Append(Bool(CaseSensitiveColumns)).Append(",\n");
        if (hash is not null)
        {
            Property(sb, 1, "snapshotSha256", hash);
        }

        sb.Append("  \"objects\": [");
        var objects = Objects.OrderBy(o => o.Schema ?? string.Empty, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal).ToList();
        for (var i = 0; i < objects.Count; i++)
        {
            var obj = objects[i];
            sb.Append(i == 0 ? "\n" : ",\n");
            sb.Append("    {\n");
            if (obj.Schema is not null)
            {
                Property(sb, 3, "schema", obj.Schema);
            }

            Property(sb, 3, "name", obj.Name);
            Property(sb, 3, "kind", obj.Kind);
            sb.Append("      \"columns\": [");
            for (var c = 0; c < obj.Columns.Count; c++)
            {
                var column = obj.Columns[c];
                sb.Append(c == 0 ? "\n" : ",\n");
                sb.Append("        { \"name\": ").Append(Quote(column.Name)).Append(", \"dbType\": ").Append(Quote(column.DbType));
                if (column.MaxLength is { } maxLength)
                {
                    sb.Append(", \"maxLength\": ").Append(maxLength.ToString(CultureInfo.InvariantCulture));
                }

                if (column.Precision is { } precision)
                {
                    sb.Append(", \"precision\": ").Append(precision.ToString(CultureInfo.InvariantCulture));
                }

                if (column.Scale is { } scale)
                {
                    sb.Append(", \"scale\": ").Append(scale.ToString(CultureInfo.InvariantCulture));
                }

                if (column.Nullable is { } nullable)
                {
                    sb.Append(", \"nullable\": ").Append(Bool(nullable));
                }

                if (column.HasDefault)
                {
                    sb.Append(", \"hasDefault\": true");
                }

                if (column.Generated)
                {
                    sb.Append(", \"generated\": true");
                }

                sb.Append(" }");
            }

            sb.Append(obj.Columns.Count == 0 ? "]\n" : "\n      ]\n");
            sb.Append("    }");
        }

        sb.Append(objects.Count == 0 ? "]" : "\n  ]");

        // Routines (stored procedures and functions) follow the objects; the key is always written, so readers can
        // tell "none" from "a snapshot made before routines were exported".
        var routines = (Routines ?? []).OrderBy(r => r.Schema ?? string.Empty, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Parameters.Count).ToList();
        sb.Append(",\n  \"routines\": [");
        for (var i = 0; i < routines.Count; i++)
        {
            var routine = routines[i];
            sb.Append(i == 0 ? "\n" : ",\n");
            sb.Append("    {\n");
            if (routine.Schema is not null)
            {
                Property(sb, 3, "schema", routine.Schema);
            }

            Property(sb, 3, "name", routine.Name);
            Property(sb, 3, "kind", routine.Kind);
            sb.Append("      \"parameters\": [");
            for (var p = 0; p < routine.Parameters.Count; p++)
            {
                var parameter = routine.Parameters[p];
                sb.Append(p == 0 ? "\n" : ",\n");
                sb.Append("        { \"name\": ").Append(Quote(parameter.Name)).Append(", \"dbType\": ").Append(Quote(parameter.DbType))
                    .Append(", \"mode\": ").Append(Quote(parameter.Mode)).Append(" }");
            }

            sb.Append(routine.Parameters.Count == 0 ? "]\n" : "\n      ]\n");
            sb.Append("    }");
        }

        sb.Append(routines.Count == 0 ? "]\n" : "\n  ]\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>Reads a snapshot file (for <c>schema diff</c>); unknown properties are ignored.</summary>
    public static DbSchema Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (Text(root, "format") != Format)
        {
            throw new FormatException($"the file is not a {Format} snapshot");
        }

        var objects = new List<DbObject>();
        if (root.TryGetProperty("objects", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var obj in list.EnumerateArray())
            {
                var columns = new List<DbColumn>();
                if (obj.TryGetProperty("columns", out var cols) && cols.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in cols.EnumerateArray())
                    {
                        columns.Add(new DbColumn(
                            Text(c, "name") ?? throw new FormatException("a column has no name"),
                            Text(c, "dbType") ?? string.Empty,
                            Int(c, "maxLength"),
                            Int(c, "precision"),
                            Int(c, "scale"),
                            Flag(c, "nullable"),
                            Flag(c, "hasDefault") == true,
                            Flag(c, "generated") == true));
                    }
                }

                objects.Add(new DbObject(Text(obj, "schema"), Text(obj, "name") ?? throw new FormatException("an object has no name"), Text(obj, "kind") ?? "table", columns));
            }
        }

        var routines = new List<DbRoutine>();
        if (root.TryGetProperty("routines", out var routineList) && routineList.ValueKind == JsonValueKind.Array)
        {
            foreach (var routine in routineList.EnumerateArray())
            {
                var parameters = new List<DbParameter>();
                if (routine.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in ps.EnumerateArray())
                    {
                        parameters.Add(new DbParameter(Text(p, "name") ?? string.Empty, Text(p, "dbType") ?? string.Empty, Text(p, "mode") ?? "in"));
                    }
                }

                routines.Add(new DbRoutine(Text(routine, "schema"), Text(routine, "name") ?? throw new FormatException("a routine has no name"), Text(routine, "kind") ?? "procedure", parameters));
            }
        }

        return new DbSchema(
            Text(root, "dialect") ?? string.Empty,
            Text(root, "databaseLogicalName"),
            Text(root, "defaultSchema"),
            Flag(root, "caseSensitiveTables") == true,
            Flag(root, "caseSensitiveColumns") == true,
            objects,
            routines);
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static bool? Flag(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    private static void Property(StringBuilder sb, int depth, string name, string value)
        => sb.Append(' ', depth * 2).Append('"').Append(name).Append("\": ").Append(Quote(value)).Append(",\n");

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }
}
