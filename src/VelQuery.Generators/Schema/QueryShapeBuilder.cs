using System;
using System.Collections.Generic;
using System.Linq;
using VelQuery.Generators.Infrastructure;
using VelQuery.Generators.Sql;

namespace VelQuery.Generators.Schema;

/// <summary>
/// Builds a <see cref="QueryShape"/> from contract SQL (ADR-0006). This is a deliberately shallow recognizer for the
/// static subset in PRD §11.3, not a SQL parser: SELECT blocks with joins, derived tables, CTEs, subqueries and set
/// operations; INSERT (column list, VALUES/SELECT/DEFAULT VALUES/SET, ON CONFLICT, ON DUPLICATE KEY, OUTPUT,
/// RETURNING); UPDATE and DELETE (including multi-table forms). Anything it does not recognize makes the affected part
/// unchecked (an opaque block or unknown source) rather than guessed, so it never produces a diagnostic it cannot
/// justify (VQ-FR-034, PRD §7.1 rule).
/// </summary>
internal sealed class QueryShapeBuilder
{
    private readonly List<SqlToken> _t;
    private readonly int[] _match;
    private readonly SqlDialect _dialect;
    private readonly SqlSourceMap? _map;
    private readonly LocationInfo? _fallback;
    private readonly List<TableReference> _tables = [];
    private readonly HashSet<int> _aliasTables = [];
    private readonly List<ScopeBuilder> _scopes = [];
    private readonly List<InsertTarget> _inserts = [];
    private readonly List<string> _unsupported = [];
    private readonly HashSet<string> _created = new(StringComparer.OrdinalIgnoreCase);

    private QueryShapeBuilder(List<SqlToken> tokens, int[] match, SqlDialect dialect, SqlSourceMap? map, LocationInfo? fallback)
    {
        _t = tokens;
        _match = match;
        _dialect = dialect;
        _map = map;
        _fallback = fallback;
    }

    /// <summary>Returns the shape, or null with a reason when the SQL cannot be analyzed at all.</summary>
    public static QueryShape? Build(string sql, SqlDialect dialect, SqlSourceMap? map, LocationInfo? fallback, out string? reason)
    {
        reason = null;
        var tokens = SqlTokenizer.Tokenize(sql, dialect);
        if (tokens is null)
        {
            reason = "the SQL contains an unterminated literal, identifier or comment";
            return null;
        }

        var match = new int[tokens.Count];
        var stack = new Stack<int>();
        for (var i = 0; i < tokens.Count; i++)
        {
            match[i] = -1;
            if (tokens[i].IsPunctuation('('))
            {
                stack.Push(i);
            }
            else if (tokens[i].IsPunctuation(')'))
            {
                if (stack.Count == 0)
                {
                    reason = "the SQL has unbalanced parentheses";
                    return null;
                }

                var open = stack.Pop();
                match[open] = i;
                match[i] = open;
            }
        }

        if (stack.Count > 0)
        {
            reason = "the SQL has unbalanced parentheses";
            return null;
        }

        var builder = new QueryShapeBuilder(tokens, match, dialect, map, fallback);
        return builder.BuildCore();
    }

    private QueryShape BuildCore()
    {
        CollectCreatedTables();
        var result = -1;
        var start = 0;
        for (var i = 0; i <= _t.Count; i = i < _t.Count ? Next(i) : i + 1)
        {
            if (i == _t.Count || _t[i].IsPunctuation(';'))
            {
                if (i > start)
                {
                    // The contract reads the first result set; INSERT/UPDATE/DELETE without OUTPUT/RETURNING return none.
                    var produced = ParseStatement(start, i, null);
                    if (result < 0)
                    {
                        result = produced;
                    }
                }

                start = i + 1;
            }
        }

        var tables = _tables.Select((t, i) => _aliasTables.Contains(i) ? t with { Location = null, Name = new SqlName(string.Empty, false) } : t);
        return new QueryShape(
            tables.ToEquatableArray(),
            _scopes.Select(s => s.ToScope()).ToEquatableArray(),
            result,
            _inserts.ToEquatableArray(),
            _unsupported.Distinct(StringComparer.Ordinal).ToEquatableArray());
    }

    // ------------------------------------------------------------------ statements

    /// <summary>
    /// Tables and views the batch creates itself (<c>CREATE [TEMP] TABLE t</c>, <c>CREATE VIEW v</c>,
    /// <c>SELECT ... INTO t</c>) are not in the snapshot; references to them are unknown sources, never VQ1005.
    /// </summary>
    private void CollectCreatedTables()
    {
        var statementStart = 0;
        for (var i = 0; i < _t.Count; i = Next(i))
        {
            var token = _t[i];
            if (token.IsPunctuation(';'))
            {
                statementStart = i + 1;
                continue;
            }

            var k = -1;
            if (token.IsWord("CREATE"))
            {
                k = i + 1;
                while (k < _t.Count && _t[k].Kind == SqlTokenKind.Word &&
                       _t[k].Text.ToUpperInvariant() is "OR" or "REPLACE" or "TEMP" or "TEMPORARY" or "GLOBAL" or "LOCAL" or "UNLOGGED" or "MATERIALIZED" or "VIRTUAL" or "ALGORITHM" or "SQL" or "SECURITY")
                {
                    k++;
                }

                if (k >= _t.Count || !(_t[k].IsWord("TABLE") || _t[k].IsWord("VIEW")))
                {
                    continue;
                }

                k++;
                if (k + 2 < _t.Count && _t[k].IsWord("IF") && _t[k + 1].IsWord("NOT") && _t[k + 2].IsWord("EXISTS"))
                {
                    k += 3;
                }
            }
            else if (token.IsWord("INTO") && statementStart < _t.Count && (_t[statementStart].IsWord("SELECT") || _t[statementStart].IsWord("WITH")))
            {
                k = i + 1; // SELECT ... INTO NewTable (SQL Server); MySQL INTO @var is a parameter, not a name.
            }

            if (k >= 0 && k < _t.Count && _t[k].IsName && TryChain(k, _t.Count, out var parts, out _))
            {
                _created.Add(parts[parts.Count - 1].Text);
            }
        }
    }

    private int ParseStatement(int s, int e, CteScope? ctes)
    {
        if (_t[s].IsWord("WITH"))
        {
            s = ParseCtes(s, e, -1, ref ctes);
            if (s < 0 || s >= e)
            {
                return -1;
            }
        }

        var first = _t[s];
        if (first.IsWord("SELECT") || first.IsWord("VALUES") || first.IsPunctuation('('))
        {
            // SELECT ... INTO creates a table or sets variables instead of returning rows.
            var query = ParseQuery(s, e, -1, ctes);
            return Scope(query).Into ? -1 : query;
        }

        if (first.IsWord("INSERT") || first.IsWord("REPLACE"))
        {
            return ParseInsert(s, e, ctes);
        }

        if (first.IsWord("UPDATE"))
        {
            return ParseUpdate(s, e, ctes);
        }

        if (first.IsWord("DELETE"))
        {
            return ParseDelete(s, e, ctes);
        }

        if (first.IsWord("CREATE"))
        {
            // DDL is not checked, but CREATE TABLE ... AS SELECT and CREATE VIEW ... AS SELECT read tables: check that query.
            for (var i = s + 1; i < e; i = Next(i))
            {
                if (_t[i].IsWord("AS") && StartsQuery(i + 1))
                {
                    ParseQuery(i + 1, e, -1, ctes);
                    break;
                }
            }

            return -1;
        }

        if (first.Kind != SqlTokenKind.Word || !SqlKeywords.Ignorable.Contains(first.Text))
        {
            _unsupported.Add($"'{first.Text.ToUpperInvariant()}' statements are not checked");
        }

        return -1;
    }

    /// <summary>Parses <c>WITH [RECURSIVE] name [(cols)] AS [NOT] [MATERIALIZED] (query), ...</c>; returns the index after it.</summary>
    private int ParseCtes(int s, int e, int parent, ref CteScope? ctes)
    {
        var i = s + 1;
        var recursive = false;
        if (i < e && _t[i].IsWord("RECURSIVE"))
        {
            recursive = true;
            i++;
        }

        while (i < e && _t[i].IsName)
        {
            var name = _t[i].Text;
            i++;
            var renames = new List<SqlName>();
            if (i < e && _t[i].IsPunctuation('('))
            {
                renames.AddRange(NamesIn(i + 1, _match[i]));
                i = _match[i] + 1;
            }

            if (i >= e || !_t[i].IsWord("AS"))
            {
                break;
            }

            i++;
            while (i < e && (_t[i].IsWord("NOT") || _t[i].IsWord("MATERIALIZED")))
            {
                i++;
            }

            if (i >= e || !_t[i].IsPunctuation('('))
            {
                break;
            }

            var close = _match[i];
            var selfReferencing = recursive || Enumerable.Range(i + 1, close - i - 1).Any(k => _t[k].IsName && string.Equals(_t[k].Text, name, StringComparison.OrdinalIgnoreCase));
            var bodyCtes = selfReferencing ? new CteScope(ctes, name, -1, []) : ctes;
            var scope = ParseQuery(i + 1, close, parent, bodyCtes);
            ctes = new CteScope(ctes, name, selfReferencing ? -1 : scope, renames);
            i = close + 1;
            if (i < e && _t[i].IsPunctuation(','))
            {
                i++;
                continue;
            }

            return i;
        }

        _unsupported.Add("a WITH clause was not understood");
        return -1;
    }

    private int ParseInsert(int s, int e, CteScope? ctes)
    {
        var scope = NewScope(-1);
        var i = s + 1;
        while (i < e && (_t[i].IsWord("IGNORE") || _t[i].IsWord("LOW_PRIORITY") || _t[i].IsWord("DELAYED") || _t[i].IsWord("HIGH_PRIORITY")))
        {
            i++;
        }

        if (i < e && _t[i].IsWord("OR"))
        {
            i += 2; // SQLite INSERT OR REPLACE/IGNORE/ABORT/FAIL/ROLLBACK.
        }

        if (i < e && _t[i].IsWord("INTO"))
        {
            i++;
        }

        i = SkipTop(i, e);
        var before = Scope(scope).Sources.Count;
        i = ParseFactor(i, e, scope, -1, ctes, allowAlias: false, allowFunction: false);
        if (i < 0 || Scope(scope).Sources.Count != before + 1)
        {
            Scope(scope).Opaque = true;
            return -1;
        }

        var target = before;
        if (i < e && _t[i].IsWord("AS") && i + 1 < e && _t[i + 1].IsName)
        {
            // PostgreSQL / SQLite target alias.
            Scope(scope).Sources[target] = Scope(scope).Sources[target] with { Alias = Name(i + 1) };
            i += 2;
        }

        AddPseudoSources(scope, target);
        var columns = new List<SqlName>();
        var hasColumnList = false;
        var columnReferencesStart = -1;
        if (i < e && _t[i].IsPunctuation('(') && !StartsQuery(i + 1))
        {
            hasColumnList = true;
            columnReferencesStart = Scope(scope).References.Count;
            foreach (var (name, k) in ChainsIn(i + 1, _match[i]))
            {
                columns.Add(name[name.Count - 1]);
                AddReference(scope, [name[name.Count - 1]], k, k + 2 * name.Count - 1, target, write: true);
            }

            i = _match[i] + 1;
        }

        var result = -1;
        if (i < e && _t[i].IsWord("OUTPUT"))
        {
            var end = FindWord(i + 1, e, "VALUES", "SELECT", "DEFAULT", "WITH", "EXEC", "EXECUTE", "INTO");
            ParseOutputs(i + 1, end, scope, ctes);
            result = scope;
            i = end;
            if (i < e && _t[i].IsWord("INTO"))
            {
                result = -1; // OUTPUT ... INTO writes rows to a table instead of returning them.
                Scope(scope).Outputs.Clear();
                i = FindWord(i + 1, e, "VALUES", "SELECT", "DEFAULT", "WITH", "EXEC", "EXECUTE");
            }
        }

        if (i + 2 < e && _t[i].IsWord("OVERRIDING"))
        {
            // PostgreSQL OVERRIDING SYSTEM|USER VALUE permits explicit values for GENERATED ALWAYS identity columns.
            // The snapshot does not tell identity from computed columns, so the listed columns are not reported as
            // generated writes (VQ1030); they are still checked for existence.
            i += 3;
            var references = Scope(scope).References;
            for (var k = Math.Max(columnReferencesStart, 0); columnReferencesStart >= 0 && k < references.Count; k++)
            {
                references[k] = references[k] with { Write = false };
            }
        }

        var allDefaults = false;
        var checkable = true;
        var bodyEnd = FindInsertBodyEnd(i, e);
        if (i < e && _t[i].IsWord("VALUES"))
        {
            var aliasAt = FindWord(i + 1, bodyEnd, "AS");
            WalkExpression(i + 1, aliasAt, OpaqueScope(), ctes);
            if (aliasAt < bodyEnd && aliasAt + 1 < bodyEnd && _t[aliasAt + 1].IsName)
            {
                // MySQL 8 row alias: VALUES (...) AS new ON DUPLICATE KEY UPDATE a = new.a.
                Scope(scope).Sources.Add(Scope(scope).Sources[target] with { Alias = Name(aliasAt + 1), Pseudo = true });
            }
        }
        else if (i < e && (_t[i].IsWord("SELECT") || _t[i].IsWord("WITH") || _t[i].IsPunctuation('(')))
        {
            ParseQuery(i, bodyEnd, -1, ctes);
        }
        else if (i + 1 < e && _t[i].IsWord("DEFAULT") && _t[i + 1].IsWord("VALUES"))
        {
            allDefaults = true;
        }
        else if (i < e && _t[i].IsWord("SET"))
        {
            hasColumnList = true;
            columns.AddRange(ParseAssignments(i + 1, bodyEnd, scope, target, ctes));
        }
        else
        {
            checkable = false;
            if (i < e)
            {
                _unsupported.Add($"INSERT ... {_t[i].Text.ToUpperInvariant()} is not checked");
            }
        }

        i = bodyEnd;
        while (i < e)
        {
            if (_t[i].IsWord("ON") && i + 1 < e && _t[i + 1].IsWord("CONFLICT"))
            {
                i += 2;
                if (i < e && _t[i].IsPunctuation('('))
                {
                    foreach (var (name, k) in ChainsIn(i + 1, _match[i]))
                    {
                        AddReference(scope, [name[name.Count - 1]], k, k + 2 * name.Count - 1, target, write: false);
                    }

                    i = _match[i] + 1;
                }

                i = FindWord(i, e, "DO");
                if (i + 2 < e && _t[i + 1].IsWord("UPDATE") && _t[i + 2].IsWord("SET"))
                {
                    var end = FindWord(i + 3, e, "WHERE", "RETURNING");
                    ParseAssignments(i + 3, end, scope, target, ctes);
                    i = end;
                    if (i < e && _t[i].IsWord("WHERE"))
                    {
                        var whereEnd = FindWord(i + 1, e, "RETURNING");
                        WalkExpression(i + 1, whereEnd, scope, ctes);
                        i = whereEnd;
                    }
                }
                else
                {
                    i = FindWord(i, e, "RETURNING");
                }
            }
            else if (_t[i].IsWord("ON") && i + 3 < e && _t[i + 1].IsWord("DUPLICATE") && _t[i + 2].IsWord("KEY") && _t[i + 3].IsWord("UPDATE"))
            {
                ParseAssignments(i + 4, e, scope, target, ctes);
                i = e;
            }
            else if (_t[i].IsWord("RETURNING"))
            {
                ParseOutputs(i + 1, e, scope, ctes);
                result = scope;
                i = e;
            }
            else
            {
                i++;
            }
        }

        if (checkable && (hasColumnList || allDefaults) && Scope(scope).Sources[target].Kind == SourceKind.Table)
        {
            _inserts.Add(new InsertTarget(scope, target, columns.ToEquatableArray(), allDefaults));
        }

        return result;
    }

    private int ParseUpdate(int s, int e, CteScope? ctes)
    {
        var scope = NewScope(-1);
        var i = s + 1;
        while (i < e && (_t[i].IsWord("LOW_PRIORITY") || _t[i].IsWord("IGNORE") || _t[i].IsWord("ONLY")))
        {
            i++;
        }

        if (i < e && _t[i].IsWord("OR"))
        {
            i += 2;
        }

        i = SkipTop(i, e);
        var set = FindWord(i, e, "SET");
        if (set >= e)
        {
            _unsupported.Add("an UPDATE without SET was not understood");
            return -1;
        }

        ParseFromList(i, set, scope, -1, ctes);
        var targets = Scope(scope).Sources.Count;
        var setEnd = FindWord(set + 1, e, "FROM", "WHERE", "OUTPUT", "RETURNING", "ORDER", "LIMIT", "OPTION");
        var i2 = setEnd;
        (int Start, int End)? output = null;
        if (i2 < e && _t[i2].IsWord("OUTPUT"))
        {
            var end = FindWord(i2 + 1, e, "FROM", "WHERE", "INTO", "OPTION");
            output = (i2 + 1, end);
            i2 = end;
            if (i2 < e && _t[i2].IsWord("INTO"))
            {
                output = null;
                i2 = FindWord(i2 + 1, e, "FROM", "WHERE", "OPTION");
            }
        }

        if (i2 < e && _t[i2].IsWord("FROM"))
        {
            var end = FindWord(i2 + 1, e, "WHERE", "RETURNING", "ORDER", "LIMIT", "OPTION");
            ParseFromList(i2 + 1, end, scope, -1, ctes);
            i2 = end;
        }

        ResolveAliasTargets(scope, targets);
        var target = targets == 1 ? 0 : -1;
        AddPseudoSources(scope, Math.Max(target, 0));
        ParseAssignments(set + 1, setEnd, scope, target, ctes);
        return FinishWriteStatement(scope, i2, e, output, ctes);
    }

    private int ParseDelete(int s, int e, CteScope? ctes)
    {
        var scope = NewScope(-1);
        var i = s + 1;
        while (i < e && (_t[i].IsWord("LOW_PRIORITY") || _t[i].IsWord("QUICK") || _t[i].IsWord("IGNORE")))
        {
            i++;
        }

        i = SkipTop(i, e);
        if (i < e && _t[i].IsWord("FROM"))
        {
            i++;
        }

        var listEnd = FindWord(i, e, "FROM", "USING", "WHERE", "OUTPUT", "RETURNING", "ORDER", "LIMIT", "OPTION");
        ParseFromList(i, listEnd, scope, -1, ctes);
        var targets = Scope(scope).Sources.Count;
        var i2 = listEnd;
        (int Start, int End)? output = null;
        if (i2 < e && _t[i2].IsWord("OUTPUT"))
        {
            var end = FindWord(i2 + 1, e, "FROM", "WHERE", "INTO", "OPTION");
            output = (i2 + 1, end);
            i2 = end;
            if (i2 < e && _t[i2].IsWord("INTO"))
            {
                output = null;
                i2 = FindWord(i2 + 1, e, "FROM", "WHERE", "OPTION");
            }
        }

        if (i2 < e && (_t[i2].IsWord("FROM") || _t[i2].IsWord("USING")))
        {
            var end = FindWord(i2 + 1, e, "WHERE", "RETURNING", "ORDER", "LIMIT", "OPTION");
            ParseFromList(i2 + 1, end, scope, -1, ctes);
            i2 = end;
        }

        ResolveAliasTargets(scope, targets);
        AddPseudoSources(scope, 0);
        return FinishWriteStatement(scope, i2, e, output, ctes);
    }

    /// <summary>WHERE, RETURNING, ORDER BY and LIMIT of UPDATE/DELETE, plus a pending OUTPUT list.</summary>
    private int FinishWriteStatement(int scope, int i, int e, (int Start, int End)? output, CteScope? ctes)
    {
        var result = -1;
        if (output is { } range)
        {
            ParseOutputs(range.Start, range.End, scope, ctes);
            result = scope;
        }

        while (i < e)
        {
            if (_t[i].IsWord("WHERE") || _t[i].IsWord("ORDER"))
            {
                var end = FindWord(i + 1, e, "RETURNING", "ORDER", "LIMIT", "OPTION");
                WalkExpression(i + 1, end, scope, ctes);
                i = end;
            }
            else if (_t[i].IsWord("RETURNING"))
            {
                ParseOutputs(i + 1, e, scope, ctes);
                result = scope;
                i = e;
            }
            else
            {
                i++;
            }
        }

        return result;
    }

    /// <summary>End of an INSERT body: <c>ON CONFLICT</c>, <c>ON DUPLICATE KEY</c> or <c>RETURNING</c> at statement level.</summary>
    private int FindInsertBodyEnd(int s, int e)
    {
        for (var i = s; i < e; i = Next(i))
        {
            if (_t[i].IsWord("RETURNING") || (_t[i].IsWord("ON") && i + 1 < e && (_t[i + 1].IsWord("CONFLICT") || _t[i + 1].IsWord("DUPLICATE"))))
            {
                return i;
            }
        }

        return e;
    }

    /// <summary>
    /// <c>UPDATE p SET .. FROM Products p</c> and <c>DELETE t1 FROM t1 JOIN ..</c> name aliases defined later: such a
    /// target becomes a pseudo copy of the aliased source and its name is not checked as a table.
    /// </summary>
    private void ResolveAliasTargets(int scope, int targets)
    {
        var sources = Scope(scope).Sources;
        for (var k = 0; k < targets; k++)
        {
            var target = sources[k];
            if (target.Kind != SourceKind.Table || _tables[target.Table].Schema is not null)
            {
                continue;
            }

            for (var m = 0; m < sources.Count; m++)
            {
                if (m != k && sources[m].Alias is { } alias && SameAlias(alias, _tables[target.Table].Name) && !(m < targets && sources[m].Kind == SourceKind.Table && _tables[sources[m].Table].Name.Text == alias.Text))
                {
                    _aliasTables.Add(target.Table);
                    sources[k] = sources[m] with { Pseudo = true };
                    break;
                }
            }
        }
    }

    /// <summary>SQL Server <c>inserted</c>/<c>deleted</c> and PostgreSQL/SQLite <c>excluded</c> rows of the target table.</summary>
    private void AddPseudoSources(int scope, int target)
    {
        var sources = Scope(scope).Sources;
        if (target >= sources.Count)
        {
            return;
        }

        var source = sources[target];
        string[] names = _dialect switch
        {
            SqlDialect.SqlServer => ["inserted", "deleted"],
            SqlDialect.PostgreSql or SqlDialect.Sqlite => ["excluded"],
            _ => [],
        };
        foreach (var name in names)
        {
            sources.Add(source with { Alias = new SqlName(name, false), Pseudo = true });
        }
    }

    /// <summary>Parses <c>col = expr, (a, b) = (..), t.col += expr</c>; returns the assigned column names.</summary>
    private List<SqlName> ParseAssignments(int s, int e, int scope, int target, CteScope? ctes)
    {
        var assigned = new List<SqlName>();
        foreach (var (a, b) in SplitCommas(s, e))
        {
            var op = -1;
            for (var k = a; k < b; k = Next(k))
            {
                if (_t[k].Kind == SqlTokenKind.Operator && IsAssignment(_t[k].Text))
                {
                    op = k;
                    break;
                }
            }

            if (op < 0)
            {
                WalkExpression(a, b, scope, ctes);
                continue;
            }

            if (_t[a].IsPunctuation('(') && _match[a] < op)
            {
                foreach (var (name, k) in ChainsIn(a + 1, _match[a]))
                {
                    assigned.Add(name[name.Count - 1]);
                    AddReference(scope, [name[name.Count - 1]], k, k + 2 * name.Count - 1, target, write: true);
                }
            }
            else if (_t[a].IsName && TryChain(a, op, out var parts, out var chainEnd) && chainEnd == op)
            {
                assigned.Add(parts[parts.Count - 1]);
                AddReference(scope, parts, a, op, parts.Count == 1 ? target : -1, write: true);
            }

            WalkExpression(op + 1, b, scope, ctes);
        }

        return assigned;
    }

    // ------------------------------------------------------------------ queries

    private int ParseQuery(int s, int e, int parent, CteScope? ctes)
    {
        if (s < e && _t[s].IsWord("WITH"))
        {
            s = ParseCtes(s, e, parent, ref ctes);
            if (s < 0 || s >= e)
            {
                return OpaqueScope();
            }
        }

        var members = new List<int>();
        var segmentStart = s;
        for (var i = s; i <= e; i = i < e ? Next(i) : i + 1)
        {
            if (i == e || (_t[i].Kind == SqlTokenKind.Word && SqlKeywords.SetOperators.Contains(_t[i].Text)))
            {
                if (i > segmentStart)
                {
                    members.Add(ParseQueryTerm(segmentStart, i, parent, ctes));
                }

                segmentStart = i + 1;
                if (i < e && i + 1 < e && (_t[i + 1].IsWord("ALL") || _t[i + 1].IsWord("DISTINCT")))
                {
                    segmentStart++;
                }
            }
        }

        if (members.Count == 1)
        {
            return members[0];
        }

        var union = NewScope(parent);
        Scope(union).Members.AddRange(members);
        Scope(union).OutputsKnown = members.Count > 0 && Scope(members[0]).OutputsKnown;
        return union;
    }

    private int ParseQueryTerm(int s, int e, int parent, CteScope? ctes)
    {
        if (_t[s].IsPunctuation('('))
        {
            return ParseQuery(s + 1, _match[s], parent, ctes);
        }

        if (_t[s].IsWord("SELECT"))
        {
            return ParseSelect(s, e, parent, ctes);
        }

        var scope = OpaqueScope();
        Scope(scope).OutputsKnown = false;
        WalkExpression(s + 1, e, scope, ctes);
        return scope;
    }

    private int ParseSelect(int s, int e, int parent, CteScope? ctes)
    {
        var scope = NewScope(parent);
        var i = s + 1;
        (int, int)? distinctOn = null;
        while (i < e)
        {
            if (_t[i].IsWord("DISTINCT"))
            {
                i++;
                if (i + 1 < e && _t[i].IsWord("ON") && _t[i + 1].IsPunctuation('('))
                {
                    distinctOn = (i + 2, _match[i + 1]);
                    i = _match[i + 1] + 1;
                }
            }
            else if (_t[i].IsWord("TOP"))
            {
                i = SkipTop(i, e);
            }
            else if (_t[i].Kind == SqlTokenKind.Word && SqlKeywords.SelectModifiers.Contains(_t[i].Text))
            {
                i++;
            }
            else
            {
                break;
            }
        }

        var clauses = new List<(string Word, int Index)>();
        for (var k = i; k < e; k = Next(k))
        {
            var token = _t[k];
            if (token.Kind != SqlTokenKind.Word || !SqlKeywords.SelectClauses.Contains(token.Text))
            {
                continue;
            }

            var word = token.Text.ToUpperInvariant();
            if (word == "FROM" && k > s && _t[k - 1].IsWord("DISTINCT"))
            {
                continue; // IS [NOT] DISTINCT FROM
            }

            if (word is "GROUP" or "ORDER" && !(k + 1 < e && _t[k + 1].IsWord("BY")))
            {
                continue; // STRING_AGG(..) WITHIN GROUP (ORDER BY ..)
            }

            if (word == "FOR" && !(k + 1 < e && _t[k + 1].Kind == SqlTokenKind.Word &&
                _t[k + 1].Text.ToUpperInvariant() is "UPDATE" or "SHARE" or "NO" or "KEY" or "XML" or "JSON" or "BROWSE" or "READ"))
            {
                continue; // NEXT VALUE FOR seq
            }

            if (clauses.Any(c => c.Word == word))
            {
                continue;
            }

            clauses.Add((word, k));
        }

        int ClauseEnd(int index)
        {
            var end = e;
            foreach (var c in clauses)
            {
                if (c.Index > index && c.Index < end)
                {
                    end = c.Index;
                }
            }

            return end;
        }

        var listEnd = clauses.Count == 0 ? e : clauses.Min(c => c.Index);
        foreach (var (word, index) in clauses)
        {
            if (word == "FROM")
            {
                ParseFromList(index + 1, ClauseEnd(index), scope, parent, ctes);
            }
        }

        ParseOutputs(i, listEnd, scope, ctes);
        foreach (var (word, index) in clauses)
        {
            switch (word)
            {
                case "WHERE":
                case "GROUP":
                case "HAVING":
                case "QUALIFY":
                case "ORDER":
                case "WINDOW":
                    WalkExpression(index + 1, ClauseEnd(index), scope, ctes);
                    break;
                case "INTO":
                    Scope(scope).OutputsKnown = false;
                    Scope(scope).Into = true;
                    break;
                case "FOR" when _t[index + 1].Text.ToUpperInvariant() is "XML" or "JSON" or "BROWSE":
                    Scope(scope).OutputsKnown = false;
                    break;
            }
        }

        if (distinctOn is { } on)
        {
            WalkExpression(on.Item1, on.Item2, scope, ctes);
        }

        return scope;
    }

    private enum JoinKind
    {
        Inner,
        Left,
        Right,
        Full,
    }

    /// <summary>Parses a FROM list (also UPDATE/DELETE target lists) into <paramref name="scope"/>'s sources.</summary>
    private void ParseFromList(int s, int e, int scope, int outerParent, CteScope? ctes)
    {
        var conditions = new List<(int, int)>();
        var i = s;
        var first = true;
        while (i < e)
        {
            var kind = JoinKind.Inner;
            var lateral = false;
            var natural = false;
            if (!first)
            {
                if (_t[i].IsPunctuation(','))
                {
                    i++;
                }
                else
                {
                    var joinStart = i;
                    while (i < e && _t[i].Kind == SqlTokenKind.Word && !_t[i].IsWord("JOIN") && !_t[i].IsWord("APPLY") && !_t[i].IsWord("STRAIGHT_JOIN"))
                    {
                        switch (_t[i].Text.ToUpperInvariant())
                        {
                            case "LEFT":
                                kind = JoinKind.Left;
                                break;
                            case "RIGHT":
                                kind = JoinKind.Right;
                                break;
                            case "FULL":
                                kind = JoinKind.Full;
                                break;
                            case "OUTER" when i + 1 < e && _t[i + 1].IsWord("APPLY"):
                                kind = JoinKind.Left;
                                break;
                            case "NATURAL":
                                natural = true;
                                break;
                            case "INNER":
                            case "CROSS":
                            case "OUTER":
                                break;
                            default:
                                Scope(scope).Opaque = true;
                                return;
                        }

                        i++;
                    }

                    if (i >= e || i == joinStart && !_t[i].IsWord("JOIN") && !_t[i].IsWord("STRAIGHT_JOIN"))
                    {
                        Scope(scope).Opaque = true;
                        return;
                    }

                    lateral = _t[i].IsWord("APPLY");
                    i++;
                }
            }

            if (i < e && _t[i].IsWord("LATERAL"))
            {
                lateral = true;
                i++;
            }

            var sources = Scope(scope).Sources;
            var before = sources.Count;
            i = ParseFactor(i, e, scope, lateral ? scope : outerParent, ctes, allowAlias: true);
            if (i < 0)
            {
                Scope(scope).Opaque = true;
                return;
            }

            if (kind is JoinKind.Left or JoinKind.Full)
            {
                MarkNullable(sources, before, sources.Count);
            }

            if (kind is JoinKind.Right or JoinKind.Full)
            {
                MarkNullable(sources, 0, before);
            }

            if (i < e && _t[i].IsWord("ON"))
            {
                var end = i + 1;
                while (end < e && !_t[end].IsPunctuation(',') && !IsJoinStart(end))
                {
                    end = Next(end);
                }

                conditions.Add((i + 1, end));
                i = end;
            }
            else if (i < e && _t[i].IsWord("USING") && i + 1 < e && _t[i + 1].IsPunctuation('('))
            {
                if (sources.Count > before)
                {
                    sources[before] = sources[before] with { Using = NamesIn(i + 2, _match[i + 1]).ToEquatableArray() };
                }

                i = _match[i + 1] + 1;
            }

            if (natural && sources.Count > before)
            {
                sources[before] = sources[before] with { Natural = true };
            }

            first = false;
        }

        foreach (var (a, b) in conditions)
        {
            WalkExpression(a, b, scope, ctes);
        }
    }

    private static void MarkNullable(List<QuerySource> sources, int from, int to)
    {
        for (var k = from; k < to; k++)
        {
            sources[k] = sources[k] with { Nullable = true };
        }
    }

    private bool IsJoinStart(int i)
        => _t[i].Kind == SqlTokenKind.Word && SqlKeywords.JoinStarts.Contains(_t[i].Text) && !(i + 1 < _t.Count && _t[i + 1].IsPunctuation('('));

    /// <summary>Parses one table factor; returns the index after it, or -1 when it is not understood.</summary>
    private int ParseFactor(int i, int e, int scope, int derivedParent, CteScope? ctes, bool allowAlias, bool allowFunction = true)
    {
        if (i >= e)
        {
            return -1;
        }

        if (_t[i].IsWord("ONLY") && ++i >= e)
        {
            return -1;
        }

        var sources = Scope(scope).Sources;
        if (_t[i].IsPunctuation('('))
        {
            var close = _match[i];
            if (StartsQuery(i + 1))
            {
                var child = ParseQuery(i + 1, close, derivedParent, ctes);
                i = close + 1;
                var (alias, renames, next) = ParseAlias(i, e, allowAlias);
                sources.Add(new QuerySource(alias, SourceKind.Derived, -1, child, renames.ToEquatableArray(), false, false));
                return SkipHints(next, e);
            }

            ParseFromList(i + 1, close, scope, derivedParent, ctes);
            var (_, _, after) = ParseAlias(close + 1, e, allowAlias);
            return SkipHints(after, e);
        }

        if (_t[i].Kind == SqlTokenKind.Parameter)
        {
            var (alias, _, next) = ParseAlias(i + 1, e, allowAlias);
            sources.Add(Unknown(alias ?? new SqlName(_t[i].Text, false)));
            return SkipHints(next, e);
        }

        if (!_t[i].IsName || !TryChain(i, e, out var parts, out var chainEnd) || parts.Count > 3)
        {
            return -1;
        }

        if (allowFunction && chainEnd < e && _t[chainEnd].IsPunctuation('('))
        {
            // Table-valued function: its arguments may reference earlier sources (LATERAL/APPLY).
            WalkExpression(chainEnd + 1, _match[chainEnd], scope, ctes);
            var (alias, _, next) = ParseAlias(_match[chainEnd] + 1, e, allowAlias);
            sources.Add(Unknown(alias ?? parts[parts.Count - 1]));
            return SkipHints(next, e);
        }

        var last = parts[parts.Count - 1];
        var (tableAlias, tableRenames, afterAlias) = ParseAlias(chainEnd, e, allowAlias);
        if ((parts.Count == 1 && (last.Text.StartsWith("#", StringComparison.Ordinal) || last.Text.Equals("DUAL", StringComparison.OrdinalIgnoreCase))) || _created.Contains(last.Text))
        {
            sources.Add(Unknown(tableAlias ?? last));
        }
        else if (parts.Count == 1 && ctes?.Find(last.Text) is { } cte)
        {
            var renames = tableRenames.Count > 0 ? tableRenames : cte.Renames;
            sources.Add(cte.Scope < 0
                ? Unknown(tableAlias ?? last)
                : new QuerySource(tableAlias ?? last, SourceKind.Derived, -1, cte.Scope, renames.ToEquatableArray(), false, false));
        }
        else
        {
            _tables.Add(new TableReference(
                parts.Count == 3 ? parts[0] : null,
                parts.Count >= 2 ? parts[parts.Count - 2] : null,
                last,
                Location(i, chainEnd)));
            sources.Add(new QuerySource(tableAlias ?? last, SourceKind.Table, _tables.Count - 1, -1, tableRenames.ToEquatableArray(), false, false));
        }

        return SkipHints(afterAlias, e);
    }

    private (SqlName? Alias, List<SqlName> Renames, int Next) ParseAlias(int i, int e, bool allowAlias)
    {
        var renames = new List<SqlName>();
        if (!allowAlias || i >= e)
        {
            return (null, renames, i);
        }

        var explicitAs = _t[i].IsWord("AS");
        var at = explicitAs ? i + 1 : i;
        if (at < e && (_t[at].Kind == SqlTokenKind.QuotedIdentifier || (_t[at].Kind == SqlTokenKind.Word && !SqlKeywords.NotAlias.Contains(_t[at].Text))))
        {
            var alias = Name(at);
            var next = at + 1;
            if (next < e && _t[next].IsPunctuation('('))
            {
                renames.AddRange(NamesIn(next + 1, _match[next]));
                next = _match[next] + 1;
            }

            return (alias, renames, next);
        }

        return (null, renames, i);
    }

    /// <summary>Skips table hints: <c>WITH (NOLOCK)</c>, MySQL index hints, TABLESAMPLE, PARTITION, FOR SYSTEM_TIME.</summary>
    private int SkipHints(int i, int e)
    {
        while (i < e)
        {
            var token = _t[i];
            if ((token.IsWord("WITH") || token.IsWord("PARTITION")) && i + 1 < e && _t[i + 1].IsPunctuation('('))
            {
                i = _match[i + 1] + 1;
            }
            else if (token.IsWord("USE") || token.IsWord("FORCE") || (token.IsWord("IGNORE") && i + 1 < e && (_t[i + 1].IsWord("INDEX") || _t[i + 1].IsWord("KEY"))))
            {
                var open = FindPunctuation(i, e, '(');
                i = open < e ? _match[open] + 1 : e;
            }
            else if (token.IsWord("TABLESAMPLE"))
            {
                var open = FindPunctuation(i, e, '(');
                i = open < e ? _match[open] + 1 : e;
                if (i + 1 < e && _t[i].IsWord("REPEATABLE") && _t[i + 1].IsPunctuation('('))
                {
                    i = _match[i + 1] + 1;
                }
            }
            else if (token.IsWord("FOR") && i + 1 < e && _t[i + 1].IsWord("SYSTEM_TIME"))
            {
                i += 2;
                while (i < e && !_t[i].IsPunctuation(',') && !IsJoinStart(i) && !_t[i].IsWord("ON") && !(_t[i].Kind == SqlTokenKind.Word && SqlKeywords.SelectClauses.Contains(_t[i].Text)))
                {
                    i = Next(i);
                }
            }
            else
            {
                break;
            }
        }

        return i;
    }

    /// <summary>Parses select-list, OUTPUT or RETURNING items into outputs and references.</summary>
    private void ParseOutputs(int s, int e, int scope, CteScope? ctes)
    {
        var builder = Scope(scope);
        foreach (var (a, b0) in SplitCommas(s, e))
        {
            var b = b0;
            if (a >= b)
            {
                continue;
            }

            SqlName? alias = null;
            var exprStart = a;
            if (b - a >= 3 && _t[b - 2].IsWord("AS") && _t[b - 1].IsName)
            {
                alias = Name(b - 1);
                b -= 2;
            }
            else if (b - a >= 3 && _t[b - 2].IsWord("AS") && _t[b - 1].Kind == SqlTokenKind.String && _dialect != SqlDialect.PostgreSql)
            {
                var text = _t[b - 1].Text;
                alias = new SqlName(text.Length >= 2 ? text.Substring(1, text.Length - 2) : text, true);
                b -= 2;
            }
            else if (b - a >= 2 && _t[b - 1].IsName && !(_t[b - 1].Kind == SqlTokenKind.Word && SqlKeywords.Expression.Contains(_t[b - 1].Text)) && EndsExpression(b - 2))
            {
                alias = Name(b - 1);
                b -= 1;
            }
            else if (_dialect == SqlDialect.SqlServer && b - a >= 3 && _t[a].IsName && _t[a + 1].IsOperator("="))
            {
                alias = Name(a); // SELECT Alias = expression
                exprStart = a + 2;
            }
            else if (_t[a].Kind == SqlTokenKind.Parameter && a + 1 < b && _t[a + 1].Kind == SqlTokenKind.Operator && IsAssignment(_t[a + 1].Text))
            {
                builder.OutputsKnown = false; // SELECT @variable = expression assigns instead of returning.
                WalkExpression(a + 2, b, scope, ctes);
                continue;
            }

            if (b - exprStart == 1 && _t[exprStart].IsOperator("*"))
            {
                builder.Outputs.Add(new QueryOutput(OutputKind.Star, null, null, EquatableArray<SqlName>.Empty));
                continue;
            }

            if (_t[exprStart].IsName && TryChain(exprStart, b, out var parts, out var chainEnd, allowStar: true))
            {
                if (chainEnd == b && parts.Count > 0 && parts[parts.Count - 1].Text == "*" && parts.Count > 1)
                {
                    builder.Outputs.Add(new QueryOutput(OutputKind.QualifiedStar, null, null, parts.Take(parts.Count - 1).ToEquatableArray()));
                    continue;
                }

                if (chainEnd == b && parts[parts.Count - 1].Text != "*" && !(parts.Count == 1 && !parts[0].Quoted && SqlKeywords.Expression.Contains(parts[0].Text)))
                {
                    var reference = AddReference(scope, parts, exprStart, b, -1, write: false);
                    builder.Outputs.Add(new QueryOutput(OutputKind.Column, alias ?? parts[parts.Count - 1], reference, EquatableArray<SqlName>.Empty));
                    continue;
                }
            }

            WalkExpression(exprStart, b, scope, ctes);
            builder.Outputs.Add(new QueryOutput(OutputKind.Expression, alias, null, EquatableArray<SqlName>.Empty));
        }
    }

    /// <summary>Whether the token at <paramref name="i"/> can end an expression (so a following name is an alias).</summary>
    private bool EndsExpression(int i)
    {
        var token = _t[i];
        return token.Kind switch
        {
            SqlTokenKind.QuotedIdentifier or SqlTokenKind.String or SqlTokenKind.Number or SqlTokenKind.Parameter => true,
            SqlTokenKind.Punctuation => token.Text == ")",
            SqlTokenKind.Word => !SqlKeywords.Expression.Contains(token.Text) || token.IsWord("END") || token.IsWord("NULL") || token.IsWord("TRUE") || token.IsWord("FALSE") || token.IsWord("CURRENT_TIMESTAMP"),
            _ => token.Text == "*" && i > 0 && _t[i - 1].IsPunctuation('.'),
        };
    }

    // ------------------------------------------------------------------ expressions

    /// <summary>Collects column references in an expression and parses its subqueries as child blocks.</summary>
    private void WalkExpression(int s, int e, int scope, CteScope? ctes)
    {
        var i = s;
        while (i < e)
        {
            var token = _t[i];
            if (token.IsPunctuation('('))
            {
                if (StartsQuery(i + 1))
                {
                    ParseQuery(i + 1, _match[i], scope, ctes);
                    i = _match[i] + 1;
                }
                else
                {
                    i++;
                }

                continue;
            }

            if (token.IsOperator("::"))
            {
                i = SkipTypeName(i + 1, e);
                continue;
            }

            if (token.Kind == SqlTokenKind.Word)
            {
                var upper = token.Text.ToUpperInvariant();
                if (upper is "COLLATE" or "OVER")
                {
                    i += 2;
                    if (upper == "OVER" && i - 1 < e && _t[i - 1].IsPunctuation('('))
                    {
                        i--; // OVER (...) is walked; only a named window is skipped.
                    }

                    continue;
                }

                if (upper == "AS")
                {
                    // CAST(x AS type), CONVERT(x USING ..): skip the type up to the closing parenthesis or comma.
                    i++;
                    while (i < e && !_t[i].IsPunctuation(')') && !_t[i].IsPunctuation(','))
                    {
                        i = Next(i);
                    }

                    continue;
                }

                if (upper == "FOR")
                {
                    i++;
                    if (i < e && _t[i].IsName && TryChain(i, e, out _, out var afterSequence))
                    {
                        i = afterSequence; // NEXT VALUE FOR sequence
                    }

                    continue;
                }
            }

            if (!token.IsName)
            {
                i++;
                continue;
            }

            if (!TryChain(i, e, out var parts, out var end, allowStar: true))
            {
                i++;
                continue;
            }

            var next = end < e ? _t[end] : default;
            if (end < e && next.IsPunctuation('(') && StartsQuery(end + 1))
            {
                // EXISTS (SELECT ..), IN (SELECT ..), ANY (SELECT ..): a subquery, not a function call.
                ParseQuery(end + 1, _match[end], scope, ctes);
                i = _match[end] + 1;
                continue;
            }

            if (end < e && next.IsPunctuation('('))
            {
                var function = parts[parts.Count - 1].Text;
                var close = _match[end];
                var argsStart = end + 1;
                if (SqlKeywords.DatePartFirst.Contains(function) && argsStart < close && _t[argsStart].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier)
                {
                    argsStart++; // DATEADD(day, ..), EXTRACT(YEAR FROM ..)
                }
                else if (function.Equals("CONVERT", StringComparison.OrdinalIgnoreCase))
                {
                    var comma = FindPunctuation(argsStart, close, ',');
                    if (_dialect == SqlDialect.SqlServer)
                    {
                        argsStart = Math.Min(comma + 1, close); // CONVERT(type, x)
                    }
                    else
                    {
                        WalkExpression(argsStart, comma, scope, ctes); // CONVERT(x, type)
                        i = close + 1;
                        continue;
                    }
                }

                WalkExpression(argsStart, close, scope, ctes);
                i = close + 1;
                continue;
            }

            if (parts[parts.Count - 1].Text == "*" ||
                (end < e && (next.Kind == SqlTokenKind.String || next.IsOperator("=>"))) ||
                (parts.Count == 1 && !parts[0].Quoted && SqlKeywords.Expression.Contains(parts[0].Text)))
            {
                i = end; // t.*, typed literal (DATE '..'), named argument, keyword
                continue;
            }

            AddReference(scope, parts, i, end, -1, write: false);
            i = end;
        }
    }

    private int SkipTypeName(int i, int e)
    {
        if (i < e && _t[i].IsName)
        {
            TryChain(i, e, out _, out i);
            while (i < e && _t[i].Kind == SqlTokenKind.Word && SqlKeywords.TypeContinuation.Contains(_t[i].Text))
            {
                i++;
            }

            if (i < e && _t[i].IsPunctuation('('))
            {
                i = _match[i] + 1;
            }

            while (i < e && (_t[i].IsOperator("[") || _t[i].IsOperator("]") || _t[i].IsOperator("[]")))
            {
                i++;
            }
        }

        return i;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Reads <c>name(.name)*</c> (optionally ending in <c>.*</c>); <paramref name="end"/> is the index after it.</summary>
    private bool TryChain(int i, int e, out List<SqlName> parts, out int end, bool allowStar = false)
    {
        parts = [Name(i)];
        end = i + 1;
        while (end + 1 < e && _t[end].IsPunctuation('.'))
        {
            var part = _t[end + 1];
            if (part.IsName)
            {
                parts.Add(Name(end + 1));
                end += 2;
            }
            else if (allowStar && part.IsOperator("*"))
            {
                parts.Add(new SqlName("*", false));
                end += 2;
                break;
            }
            else
            {
                break;
            }
        }

        return parts.Count <= 4;
    }

    /// <summary>Simple names in a parenthesized list (CTE or alias column lists).</summary>
    private IEnumerable<SqlName> NamesIn(int s, int e)
    {
        for (var k = s; k < e; k++)
        {
            if (_t[k].IsName)
            {
                yield return Name(k);
            }
        }
    }

    /// <summary>Column chains in a parenthesized column list, with the index of each chain's first token.</summary>
    private IEnumerable<(List<SqlName> Name, int Index)> ChainsIn(int s, int e)
    {
        foreach (var (a, b) in SplitCommas(s, e))
        {
            if (a < b && _t[a].IsName && TryChain(a, b, out var parts, out var end) && end == b)
            {
                yield return (parts, a);
            }
        }
    }

    private IEnumerable<(int Start, int End)> SplitCommas(int s, int e)
    {
        var start = s;
        for (var i = s; i < e; i = Next(i))
        {
            if (_t[i].IsPunctuation(','))
            {
                yield return (start, i);
                start = i + 1;
            }
        }

        yield return (start, e);
    }

    /// <summary>Next index at the same nesting depth.</summary>
    private int Next(int i) => _t[i].IsPunctuation('(') ? _match[i] + 1 : i + 1;

    private int FindWord(int s, int e, params string[] words)
    {
        for (var i = s; i < e; i = Next(i))
        {
            if (_t[i].Kind == SqlTokenKind.Word && words.Any(w => _t[i].IsWord(w)))
            {
                return i;
            }
        }

        return e;
    }

    private int FindPunctuation(int s, int e, char c)
    {
        for (var i = s; i < e; i = Next(i))
        {
            if (_t[i].IsPunctuation(c))
            {
                return i;
            }
        }

        return e;
    }

    private bool StartsQuery(int i)
    {
        while (i < _t.Count && _t[i].IsPunctuation('('))
        {
            i++;
        }

        return i < _t.Count && (_t[i].IsWord("SELECT") || _t[i].IsWord("WITH") || _t[i].IsWord("VALUES"));
    }

    private int SkipTop(int i, int e)
    {
        if (i < e && _t[i].IsWord("TOP"))
        {
            i++;
            i = i < e && _t[i].IsPunctuation('(') ? _match[i] + 1 : i + 1;
            while (i < e && (_t[i].IsWord("PERCENT") || (_t[i].IsWord("WITH") && i + 1 < e && _t[i + 1].IsWord("TIES"))))
            {
                i += _t[i].IsWord("WITH") ? 2 : 1;
            }
        }

        return i;
    }

    private static bool IsAssignment(string op) => op == "=" || (op.Length == 2 && op[1] == '=' && "+-*/%&|^".IndexOf(op[0]) >= 0);

    private static bool SameAlias(SqlName alias, SqlName name) => string.Equals(alias.Text, name.Text, StringComparison.OrdinalIgnoreCase);

    private SqlName Name(int i) => new(_t[i].Text, _t[i].Kind == SqlTokenKind.QuotedIdentifier);

    private static QuerySource Unknown(SqlName alias) => new(alias, SourceKind.Unknown, -1, -1, EquatableArray<SqlName>.Empty, false, false);

    private ColumnReference AddReference(int scope, List<SqlName> parts, int startToken, int endToken, int target, bool write)
    {
        // Point at the column name itself: in "u.Nmae" the misspelling is the last part.
        var reference = new ColumnReference(parts.ToEquatableArray(), Location(endToken - 1, endToken), target, write);
        Scope(scope).References.Add(reference);
        return reference;
    }

    private LocationInfo? Location(int startToken, int endToken)
    {
        var start = _t[startToken].Start;
        var last = _t[endToken - 1];
        return _map?.Map(start, last.Start + last.Length - start) ?? _fallback;
    }

    private int NewScope(int parent)
    {
        _scopes.Add(new ScopeBuilder(parent));
        return _scopes.Count - 1;
    }

    private int OpaqueScope()
    {
        var scope = NewScope(-1);
        Scope(scope).Opaque = true;
        return scope;
    }

    private ScopeBuilder Scope(int index) => _scopes[index];

    private sealed class ScopeBuilder(int parent)
    {
        public int Parent { get; } = parent;

        public bool Opaque { get; set; }

        public bool OutputsKnown { get; set; } = true;

        /// <summary>SELECT ... INTO: the block returns no rows.</summary>
        public bool Into { get; set; }

        public List<QuerySource> Sources { get; } = [];

        public List<ColumnReference> References { get; } = [];

        public List<QueryOutput> Outputs { get; } = [];

        public List<int> Members { get; } = [];

        public QueryScope ToScope() => new(Parent, Opaque, Sources.ToEquatableArray(), References.ToEquatableArray(), Outputs.ToEquatableArray(), OutputsKnown, Members.ToEquatableArray());
    }

    /// <summary>CTE names in scope, innermost first.</summary>
    private sealed class CteScope(CteScope? parent, string name, int scope, List<SqlName> renames)
    {
        public int Scope { get; } = scope;

        public List<SqlName> Renames { get; } = renames;

        public CteScope? Find(string candidate)
        {
            for (var c = this; c is not null; c = c.Parent)
            {
                if (string.Equals(c.Name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return c;
                }
            }

            return null;
        }

        private CteScope? Parent { get; } = parent;

        private string Name { get; } = name;
    }
}
