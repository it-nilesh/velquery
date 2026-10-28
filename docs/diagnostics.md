# VelQuery compiler diagnostics

IDs are stable. VQ1001–VQ1011 follow PRD §7.1; VQ1012 and above are VelQuery additions for query contract shape
and schema checks. Schema diagnostics (VQ1005, VQ1011, VQ1025–VQ1031) appear only when the project has a schema
snapshot; see [schema-checked SQL](schema-snapshots.md).
Generator diagnostics come from `VelQuery.Generators` (`[VelQuerySql]` contracts); VQ1008 comes from
`VelQuery.Analyzers` (call sites). Both ship in the `VelQuery.Core` package.

| ID | Severity | Source | Trigger | Fix |
|---|---|---|---|---|
| VQ1001 | Error | Generator | SQL references `@Name` that no method parameter binds (reported on the placeholder when the literal maps 1:1, else on the `sql` argument) | Add a typed parameter or fix the spelling |
| VQ1002 | Warning | Generator | A method parameter is not referenced by the SQL (session and `CancellationToken` excluded); it is not bound | Remove it or reference it |
| VQ1003 | Error | Generator | Row type cannot be mapped: not a class/record/struct, abstract, generic, inaccessible, no public constructor, ambiguous widest constructor, non-scalar member, nullable struct row, or `Scalar` with a non-scalar type | Use a mappable record/class, `[NotMapped]`, or a scalar |
| VQ1004 | Error | Generator | Two members map to the same column name (case-insensitive, `[Column]` aware) | Rename or give distinct `[Column]` names |
| VQ1008 | Error | Analyzer | `sql` argument of a session method is an interpolated string with non-constant holes or a concatenation of non-constants | Constant SQL + `QueryParameters`; allowlist identifiers |
| VQ1012 | Error | Generator | Contract type is not `partial`, is nested in a non-partial type, or is generic | Declare `partial` non-generic types |
| VQ1013 | Error | Generator | Contract declares zero or several partial method definitions without a body | Declare exactly one |
| VQ1014 | Error | Generator | Invalid method: session not first / missing / duplicated, `CancellationToken` not last, `ref`/`in`/`out`/`params`, generic, no accessibility, virtual/override | Use `public static partial Task<T> Name(IVelQuerySession session, ..., CancellationToken ct = default)` |
| VQ1015 | Error | Generator | Return type does not fit the cardinality (see `VelQuerySqlAttribute` remarks), e.g. `OrDefault` with a non-nullable result, `NonQuery` without `Task<int>`, `ValueTask` | Change the return type or `Cardinality` |
| VQ1016 | Error | Generator | Operation is ambiguous: `Task<int>` with `Default`, or a reference-type result in a nullable-oblivious context | Set `Cardinality` explicitly |
| VQ1017 | Error | Generator | Empty `queryId`/`sql`, negative `CommandTimeoutSeconds`, undefined `Cardinality`, `CommandType.TableDirect` | Fix the attribute argument |
| VQ1018 | Error | Generator | Parameter CLR type has no inferable `DbType` | Use a supported type or `[VelQueryParameter(DbType = ...)]` |
| VQ1019 | Warning | Generator | Unterminated string literal, quoted identifier or block comment in SQL; later placeholders unchecked | Fix the SQL text |
| VQ1020 | Error | Generator | Two method parameters bind the same SQL parameter (for example via `[VelQueryParameter(Name)]`) | Remove the duplicate |
| VQ1021 | Error | Generator | Internal generator failure (reported instead of crashing the build) | Report the issue |
| VQ1022 | Warning | Generator | A placeholder style VelQuery does not bind (`?`, `?NNN`, `:name`, `$name`, `$1`, depending on the dialect); it would receive no value at run time | Use `@name` placeholders |
| VQ1023 | Warning | Generator | The same placeholder is spelled with different casing (`@Id` and `@id`). In the SQLite dialect each spelling is bound so the query still works | Use one spelling |
| VQ1024 | Warning | Generator | Unknown dialect in the `VelQuerySqlDialect` MSBuild property or `[VelQuerySql(Dialect = ...)]`; generic rules are used | Use `sqlserver`, `postgresql`, `mysql`, `sqlite` or `generic` |
| VQ1005 | Error | Generator (snapshot) | A table, view or column is not in the schema snapshot; points at the name, with a "did you mean" suggestion | Fix the name, or refresh the snapshot if the database changed |
| VQ1011 | Info | Generator (snapshot) | Part of the SQL is outside the checked subset (for example `MERGE`, `FOR JSON`, stored procedures), or the snapshot dialect differs from the query's | None needed; cover it with an integration test |
| VQ1025 | Error | Generator (snapshot) | A constructor parameter or `init`/`required` member has no result column | Select or alias the column, or make the member an optional settable property |
| VQ1026 | Warning | Generator (snapshot) | A column that can be NULL (also via outer joins) maps to a non-nullable member; strict mapping throws on NULL | Make the member nullable, or return a non-NULL value (`COALESCE`) |
| VQ1027 | Warning | Generator (snapshot) | The column's database type can never convert losslessly to the member type | Change the member type or `CAST` in SQL |
| VQ1028 | Error | Generator (snapshot) | Two result columns have the same name (for example `u.Id`, `o.Id`) | Alias one of them |
| VQ1029 | Warning | Generator (snapshot) | An INSERT omits a NOT NULL column without a default | Add the column to the INSERT |
| VQ1030 | Warning | Generator (snapshot) | An INSERT or UPDATE writes a computed, `GENERATED ALWAYS` or `rowversion` column | Remove the column from the write |
| VQ1031 | Warning | Generator (snapshot) | The snapshot file is invalid, has an unknown format, or the project has more than one | Regenerate with `velquery schema snapshot` |

## SQL dialects

The SQL scanner is lexical, not a parser, and follows the project's dialect: the `VelQuerySqlDialect` MSBuild property,
which each provider package sets by default (`VelQuery.SqlServer` → `sqlserver`, `VelQuery.PostgreSQL` → `postgresql`,
`VelQuery.MySql` → `mysql`, `VelQuery.Sqlite` → `sqlite`), or `[VelQuerySql(Dialect = "...")]` on one contract. Set the
property yourself when a project references several providers. Unset means `sqlserver`.

| Dialect | Skipped (no placeholders inside) | Also understood |
|---|---|---|
| all | `'...'` strings with `''`, `--` comments, `/* */` comments | `@Name` placeholders (letters, digits, `_`) |
| `sqlserver` | `[...]` and `"..."` identifiers, `@@` functions | nested block comments |
| `postgresql` | `"..."` identifiers, `$$...$$`/`$tag$...$tag$` bodies, `E'...'` with backslash escapes | nested block comments, `::` casts; `[...]` is an array subscript and `?`/`@@` are operators, so placeholders inside them are found; `$1` → VQ1022 |
| `mysql` | backslash escapes in `'...'` and `"..."` strings, backtick identifiers, `#` comments, `-- ` comments (space required), `@@` variables | `/*! ... */` executable comments are scanned; `@var` user variables (assigned with `:=`, `SET` or `INTO`) are not parameters; `?`/`:name` → VQ1022 |
| `sqlite` | `[...]`, backtick and `"..."` identifiers | non-nesting block comments; `?`, `?NNN`, `:name`, `$name` → VQ1022; spellings that differ in case are bound separately (SQLite binds case-sensitively) |
| `generic` | `[...]`, backtick and `"..."` identifiers | `?`/`:name` → VQ1022 |

Not understood in any dialect: `DECLARE @local` variables in T-SQL batches (reported as VQ1001) and SQL built inside
`EXEC('...')`. Stored procedure contracts (`CommandType.StoredProcedure`) bind every method parameter by name and skip
the scan.
