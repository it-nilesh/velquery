# ADR-0005: Parameter metadata and provider adapters

- **Status:** Proposed
- **Date:** 2026-10-10
- **Deciders:** Provider lead, Principal Architect
- **PRD references:** PR-05, VQ-FR-007, -008, -022..-024, -042, §9.2, §9.4, §10.1, VQ1006, D-04, D-11
- **Code:** `src/VelQuery.Abstractions/Parameters/ParameterSpec.cs`, `Parameters/DbTypeInference.cs`,
  `Parameters/ParameterBinder.cs` (`ParameterWriter`), `Providers/IVelQueryProvider.cs`,
  `src/VelQuery.Core/Parameters/QueryParameters.cs`, `src/VelQuery.SqlServer/SqlServerProvider.cs`

## Context

On SQL Server, loosely typed parameters cause implicit conversions (index scans) and plan-cache fragmentation: SqlClient
derives `nvarchar(n)` from each string's length, so `@Name nvarchar(5)` and `@Name nvarchar(6)` are different cached
plans. Values must always be bound, never concatenated (VQ-SEC-001). Generic code must stay driver-agnostic while
each provider applies native knowledge.

## Decision

**`ParameterSpec`** (immutable `readonly record struct`): `Name` (no prefix), `DbType?`, `Size?` (`-1` = MAX),
`Precision?`, `Scale?`, `Direction`, `ProviderDbType?` (an `int` the provider interprets, e.g. a `SqlDbType`). Specs
are compile-time constants in generated binders and are built from method arguments in `QueryParameters`.

**Type inference (`DbTypeInference.Infer`)**, used when no `DbType` is given, by `TypeCode`, no reflection: bool,
signed/unsigned integers, `float`/`double`/`decimal` → their `DbType`; `string` → `String`; `char` →
`StringFixedLength`; `DateTime` → `DateTime2` (not legacy `datetime`); `Guid`, `DateTimeOffset`; `TimeSpan`/`TimeOnly`
→ `Time`; `DateOnly` → `Date`; `byte[]` → `Binary`; enums → underlying type; unknown → `null` (driver infers from
the value). The generator applies the same table at compile time and reports VQ1006 when a string/decimal lacks
size/precision.

**Binding (`ParameterWriter.Add`).** Creates the parameter from the command, sets the provider-formatted name,
direction and every specified generic facet, then calls `IVelQueryProvider.ConfigureParameter`, then assigns the
value. `null` becomes `DBNull.Value` with the type metadata retained. Parameters are created per invocation and never
cached or reused (§8.2).

**Name normalization (`QueryParameters`).** One leading `@`, `:`, `$` or `?` is stripped; the remainder must be
non-empty, start with a letter or `_`, and contain only letters, digits and `_`; names are unique
case-insensitively. SQL text is never rewritten.

**SQL Server provider.** `FormatParameterName` adds `@`. `ConfigureParameter` sets the native `SqlDbType` when
`ProviderDbType` is given. When no `Size` is declared it buckets instead of using the value's length: Unicode strings
→ 4000, or MAX if longer; ANSI strings and `byte[]` → 8000, or MAX; typed NULL strings/binary → 4000/8000. A declared
`Size` always wins. `QuoteIdentifier` accepts 1–128 characters and escapes `]` (identifiers must already be
allowlisted, VQ-FR-042). `Classify` maps `SqlException.Number == -2` to `Timeout`.

**No implicit collection expansion.** `IN (@Ids)` with an array is not expanded (§9.4). Arrays/TVPs will be explicit
provider extensions.

**Output parameters.** `QueryParameters.AddOutput<T>` returns an `OutputParameter<T>` handle that is populated only
after the command has fully completed — after `ExecuteNonQuery`/`ExecuteScalar`, after a buffered or streamed reader
has consumed all result sets, or after the last result of a multi-result reader — and before the command is
disposed. Values go through `ValueConverter` (strict). Reading `Value` before completion throws.

**`IVelQueryProvider` responsibilities:** dialect ID, `ProviderCapabilities` flags, parameter naming, provider-native
parameter configuration (must not log values), identifier quoting, exception classification. The PRD sketch's
`CreateParameter(in ParameterSpec, object?)` was split into `FormatParameterName` + `ConfigureParameter` so the
generic `ParameterWriter` (and generated binders) can create parameters through `DbCommand.CreateParameter()` while
the provider only adds what it knows.

**PostgreSQL** (Npgsql) is deferred to wave 2 (D-04): it needs its own naming (`@name` vs positional `$1`), arrays,
`NpgsqlParameter<T>` (avoids boxing), timestamp semantics, and must rely on Npgsql auto-prepare rather than a VelQuery
statement cache (§9.5).

## Consequences

- Strings without a declared size produce at most three plan shapes per parameter on SQL Server instead of one per
  length. Declaring the column's real size is still better (smaller memory grants, exact semantics).
- Decimals without precision/scale are **not** bucketed: SqlClient derives `decimal(p,s)` from each value, which can
  fragment the plan cache. Generated binders should always emit precision/scale (VQ1006); runtime users must pass them.
- `ParameterWriter.Add` takes `object? value`, so value-type arguments are boxed once per parameter per call. This is
  inherent to `DbParameter.Value` on SqlClient; typed parameters (Npgsql) can avoid it later.
- `DbTypeInference` maps `sbyte`/`ushort`/`uint`/`ulong` to `DbType` values SqlClient does not support; such
  parameters fail at execution time. The analyzer should flag them for the SQL Server dialect.
- `ProviderCapabilities.Prepare` is advertised by the SQL Server provider, but nothing in Core prepares commands
  today (D-11: driver-native and opt-in only after benchmarks).

## Alternatives considered

- **Size = value length (SqlClient default).** Precise per call, but fragments the plan cache. Rejected.
- **Always `nvarchar(max)`.** One plan, but MAX parameters can change plans and memory grants and block some index
  seeks. Rejected as a default; available by declaring `Size = -1`.
- **Implicit `IN` list expansion (Dapper).** Convenient, but every list length is a new SQL text and plan, and it
  rewrites SQL. Rejected for v1.0.
- **Provider creates the parameter (`CreateParameter`).** Lets the provider use typed parameters, but every binder
  then depends on the provider for construction. Deferred; may return for `NpgsqlParameter<T>`.

## Open questions

- Bucket decimals (e.g. default `decimal(38, s)` with the value's scale) or require explicit precision/scale for
  runtime SQL? (Provider lead, Gate B)
- `char` infers `StringFixedLength`, which the SQL Server provider buckets to `nchar(4000)`; should it be `nchar(1)`?
  (Provider lead, Gate B)
- `IVelQueryProvider.ConfigureParameter` says it must not read the value "beyond its type", but the SQL Server
  provider reads string/byte array lengths to pick a bucket. Clarify the rule as "may read size, must not log or
  persist the value". (Architect, Gate A)
- Table-valued parameters and PostgreSQL arrays: API shape for explicit set parameters. (Provider lead, wave 2)
