using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VelQuery.Generators.Diagnostics;
using VelQuery.Generators.Infrastructure;
using VelQuery.Generators.Sql;

namespace VelQuery.Generators.Schema;

/// <summary>A column in a <see cref="SchemaSnapshot"/>.</summary>
/// <remarks>
/// <para><c>Nullable</c>: Whether the column accepts NULL; null when the database does not report it (for example view columns).</para>
/// <para><c>HasDefault</c>: Whether an INSERT may omit the column (default, identity, serial, auto-increment, rowid).</para>
/// <para><c>Generated</c>: Whether the database computes the value, so it cannot be written (computed, GENERATED ALWAYS, rowversion).</para>
/// </remarks>
internal sealed record SchemaColumn(string Name, string? DbType, bool? Nullable, bool HasDefault, bool Generated);

/// <summary>A table or view in a <see cref="SchemaSnapshot"/>.</summary>
internal sealed record SchemaObject(string? Schema, string Name, string Kind, EquatableArray<SchemaColumn> Columns)
{
    public string Display => Schema is null ? Name : Schema + "." + Name;
}

/// <summary>
/// A parsed offline schema snapshot (<c>*.vqschema.json</c>, format <c>velquery.schema.v1</c>, PRD §11.2, ADR-0006).
/// Metadata only: names, types, nullability and defaults. The generator never contacts a database (VQ-FR-035).
/// </summary>
internal sealed record SchemaSnapshot(
    string Path,
    SqlDialect Dialect,
    string? DefaultSchema,
    bool CaseSensitiveTables,
    bool CaseSensitiveColumns,
    EquatableArray<SchemaObject> Objects);

/// <summary>The project's snapshot (if one is usable) and problems found while reading snapshot files.</summary>
internal sealed record SnapshotResult(SchemaSnapshot? Snapshot, EquatableArray<DiagnosticInfo> Problems);

/// <summary>Reads snapshot files. Unknown properties are ignored so newer tooling can add metadata.</summary>
internal static class SchemaSnapshotReader
{
    public const string Format = "velquery.schema.v1";

    public const string Extension = ".vqschema.json";

    public static bool IsSnapshotPath(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    public static SnapshotResult Read(IReadOnlyList<(string Path, string? Text)> files)
    {
        if (files.Count == 0)
        {
            return new SnapshotResult(null, EquatableArray<DiagnosticInfo>.Empty);
        }

        if (files.Count > 1)
        {
            var names = string.Join(", ", files.Select(f => FileName(f.Path)).OrderBy(n => n, StringComparer.Ordinal));
            return Problem(files[0].Path, $"a project can use one schema snapshot, but {files.Count} were found ({names})");
        }

        var (path, text) = files[0];
        if (text is null)
        {
            return Problem(path, "the file could not be read");
        }

        try
        {
            var root = MiniJson.Parse(text) as Dictionary<string, object?>;
            if (root is null)
            {
                return Problem(path, "the file is not a JSON object");
            }

            if (Str(root, "format") != Format)
            {
                return Problem(path, $"'format' must be \"{Format}\"");
            }

            if (!SqlDialects.TryParse(Str(root, "dialect"), out var dialect) || string.IsNullOrEmpty(Str(root, "dialect")) || dialect == SqlDialect.Generic)
            {
                return Problem(path, "'dialect' must be sqlserver, postgresql, mysql or sqlite");
            }

            var objects = new List<SchemaObject>();
            foreach (var item in List(root, "objects"))
            {
                if (item is not Dictionary<string, object?> obj || Str(obj, "name") is not { Length: > 0 } name)
                {
                    return Problem(path, "every object needs a 'name'");
                }

                var columns = new List<SchemaColumn>();
                foreach (var c in List(obj, "columns"))
                {
                    if (c is not Dictionary<string, object?> column || Str(column, "name") is not { Length: > 0 } columnName)
                    {
                        return Problem(path, $"every column of '{name}' needs a 'name'");
                    }

                    columns.Add(new SchemaColumn(columnName, Str(column, "dbType"), column.TryGetValue("nullable", out var n) ? n as bool? : null,
                        column.TryGetValue("hasDefault", out var d) && d is true, column.TryGetValue("generated", out var g) && g is true));
                }

                objects.Add(new SchemaObject(Str(obj, "schema"), name, Str(obj, "kind") ?? "table", columns.ToEquatableArray()));
            }

            var snapshot = new SchemaSnapshot(path, dialect, Str(root, "defaultSchema"),
                root.TryGetValue("caseSensitiveTables", out var tables) && tables is true,
                root.TryGetValue("caseSensitiveColumns", out var cols) && cols is true,
                objects.ToEquatableArray());
            return new SnapshotResult(snapshot, EquatableArray<DiagnosticInfo>.Empty);
        }
        catch (FormatException ex)
        {
            return Problem(path, "it is not valid JSON (" + ex.Message + ")");
        }
    }

    private static SnapshotResult Problem(string path, string message)
        => new(null, new[] { DiagnosticInfo.Create(Descriptors.SchemaSnapshotProblem, null, FileName(path), message) }.ToEquatableArray());

    private static string FileName(string path)
    {
        var slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return slash < 0 ? path : path.Substring(slash + 1);
    }

    private static string? Str(Dictionary<string, object?> obj, string key) => obj.TryGetValue(key, out var v) ? v as string : null;

    private static List<object?> List(Dictionary<string, object?> obj, string key) => obj.TryGetValue(key, out var v) && v is List<object?> list ? list : [];

    /// <summary>A strict, minimal JSON reader (objects, arrays, strings, numbers, literals).</summary>
    private sealed class MiniJson
    {
        private readonly string _s;
        private int _i;

        private MiniJson(string s) => _s = s;

        public static object? Parse(string text)
        {
            var reader = new MiniJson(text);
            if (text.Length > 0 && text[0] == '﻿')
            {
                reader._i = 1;
            }

            var value = reader.Value(0);
            reader.Space();
            if (reader._i != text.Length)
            {
                throw reader.Error("unexpected content after the root value");
            }

            return value;
        }

        private object? Value(int depth)
        {
            if (depth > 64)
            {
                throw Error("nesting is too deep");
            }

            Space();
            if (_i >= _s.Length)
            {
                throw Error("unexpected end of file");
            }

            switch (_s[_i])
            {
                case '{':
                    _i++;
                    var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
                    Space();
                    if (Peek() == '}')
                    {
                        _i++;
                        return obj;
                    }

                    while (true)
                    {
                        Space();
                        var key = String();
                        Space();
                        Expect(':');
                        obj[key] = Value(depth + 1);
                        Space();
                        if (Peek() == ',')
                        {
                            _i++;
                            continue;
                        }

                        Expect('}');
                        return obj;
                    }

                case '[':
                    _i++;
                    var list = new List<object?>();
                    Space();
                    if (Peek() == ']')
                    {
                        _i++;
                        return list;
                    }

                    while (true)
                    {
                        list.Add(Value(depth + 1));
                        Space();
                        if (Peek() == ',')
                        {
                            _i++;
                            continue;
                        }

                        Expect(']');
                        return list;
                    }

                case '"':
                    return String();
                case 't':
                    Literal("true");
                    return true;
                case 'f':
                    Literal("false");
                    return false;
                case 'n':
                    Literal("null");
                    return null;
                default:
                    var start = _i;
                    while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] is '-' or '+' or '.' or 'e' or 'E'))
                    {
                        _i++;
                    }

                    if (start == _i || !double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        throw Error("unexpected character");
                    }

                    return number;
            }
        }

        private string String()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (_i < _s.Length)
            {
                var c = _s[_i++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (_i >= _s.Length)
                {
                    break;
                }

                var e = _s[_i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u' when _i + 4 <= _s.Length && int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                        sb.Append((char)code);
                        _i += 4;
                        break;
                    default:
                        throw Error("invalid escape sequence");
                }
            }

            throw Error("unterminated string");
        }

        private void Literal(string word)
        {
            if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0)
            {
                throw Error("unexpected character");
            }

            _i += word.Length;
        }

        private void Expect(char c)
        {
            if (Peek() != c)
            {
                throw Error($"expected '{c}'");
            }

            _i++;
        }

        private char Peek() => _i < _s.Length ? _s[_i] : '\0';

        private void Space()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
            {
                _i++;
            }
        }

        private FormatException Error(string message)
        {
            var line = 1;
            for (var k = 0; k < _i && k < _s.Length; k++)
            {
                if (_s[k] == '\n')
                {
                    line++;
                }
            }

            return new FormatException($"{message} at line {line.ToString(CultureInfo.InvariantCulture)}");
        }
    }
}
