# ADR-0004: Mapping semantics

- **Status:** Proposed
- **Date:** 2026-10-10
- **Deciders:** Tech Lead, Compiler lead
- **PRD references:** D-06, VQ-FR-014..-017, -019, -029, §9.3, §9.5, §18.1
- **Code:** `src/VelQuery.Abstractions/Mapping/ValueConverter.cs`, `Mapping/ScalarTypes.cs`,
  `Mapping/ScalarRowMapperFactory.cs`, `Mapping/IRowMapper.cs`, `Errors/VelQueryMappingException.cs`,
  `src/VelQuery.Core/Mapping/ReflectionRowMapperFactory.cs`

## Context

Mapping is where silent data corruption happens: a NULL becomes `0`, a `bigint` is truncated, a column is bound to
the wrong member after a schema change, a string is parsed with the server's culture. Generated mappers and the
reflection fallback must behave identically, otherwise enabling the generator changes results. The reflection mapper
is the executable specification today; the generator must match it.

## Decision

**Shape resolution.** `IRowMapperFactory<T>.Create(reader, context)` runs once per reader shape per invocation
(each execution and each result set of a multi-result reader), before the first row, and resolves ordinals for that
shape. Nothing is cached across calls except the immutable per-type plan (PRD §9.5 "per invocation in the initial
release"); this prevents stale ordinals when different SQL feeds the same type.

**Scalar targets.** Primitives, enums, `string`, `decimal`, `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan`,
`DateOnly`, `TimeOnly`, `byte[]` and their nullable forms map from the first column (`ScalarRowMapperFactory`).

**Column-to-member matching.** Case-insensitive ordinal comparison (`StringComparer.OrdinalIgnoreCase`) of the result
column name against `[Column("name")]` (`System.ComponentModel.DataAnnotations.Schema`) or the member name. No naming
convention translation (no snake_case). Constructor parameters are matched to properties of the same name so that a
`[Column]` on the property applies; positional records need `[property: Column("...")]`.

**Construction.** A public parameterless constructor plus public settable (including `init`) properties is preferred.
Otherwise the public constructor with the most parameters is used (records); every one of its parameters must be
present in the result. Properties already bound through the constructor are not set again.

**Presence rules.** Constructor parameters and `required` members must be present
(`VelQueryMappingException.MissingColumn`); other settable members are optional; **extra columns are ignored**. If
two result columns match the same member (case-insensitively), mapping fails as ambiguous (VQ-FR-015) — duplicate
columns that match no member are ignored.

**NULL handling (strict by default, D-06).** A database NULL into a non-nullable value type, or into a reference type
annotated non-nullable (`NullabilityInfoContext` write state `NotNull`), throws
`VelQueryMappingException.NullToNonNullable` when `StrictMapping` is on. Reference types in nullable-oblivious code
are treated as nullable. With `StrictMapping = false`, NULL becomes `default`.

**Conversions (`ValueConverter`, lossless only).** Exactly these:

| From provider value | To | Rule |
|---|---|---|
| same type (or assignable) | target | as is |
| any integral (`sbyte`…`ulong`) | any integral | `checked`; overflow → mapping error |
| any integral | `decimal` | exact |
| integral except `long`/`ulong` | `double` | exact (`long` → `double` can lose precision, so refused) |
| `float` | `double` | exact |
| integral | enum | `Enum.ToObject` on the integral value |

Everything else fails with `VelQueryMappingException.Unconvertible` naming query, column, source and target type: no
string parsing, no `ToString`, no `Convert.ChangeType`, no `double`→`float`, `decimal`→`double`,
`DateTime`↔`DateTimeOffset` or string→enum/Guid. Because strings are never parsed, ambient culture cannot affect
results (VQ-FR-029).

**Errors** identify query ID, column and target type, never the offending value (VQ-FR-017).

**Generated mappers** follow the same rules, with typed getters (`GetInt32`, `GetString`, …) and an `IsDBNull` check
only where NULL is possible or must be rejected.

## Consequences

- Results are identical whichever mapper runs; differential tests (generated vs reflection vs raw reader) are cheap to
  write and should be part of the generator's golden suite.
- Strictness surfaces schema drift as exceptions instead of wrong data. Users migrating from Dapper will see failures
  where Dapper silently converted (e.g. `bigint` column into an `int` member works only when the value fits; a
  `decimal` column into `double` fails).
- Per-invocation ordinal resolution costs a `GetOrdinal` per mapped column per call on the generated path, and a
  name dictionary per call on the reflection path. Caching per reader shape is a later, benchmark-justified
  optimisation (PRD §19.2).
- The scalar mapper reads through `GetValue(0)` and `ValueConverter`, which boxes value types once per row.
- Enum values are not checked against defined members; out-of-range integers produce undefined enum values (as
  Dapper does).

## Alternatives considered

- **Exact-case matching only.** Safer against ambiguity but hostile to SQL Server's case-insensitive identifiers.
  Rejected; ambiguity is detected instead.
- **Fail on extra columns.** Catches `SELECT *` overfetching but breaks additive schema changes. Rejected; analyzer
  VQ1005 and schema snapshots are the place for overfetch warnings.
- **Dapper-style permissive conversion (`Convert.ChangeType`, string parsing).** Culture- and precision-sensitive,
  not AOT-friendly. Rejected (VQ-FR-029, §9.3 rule 5).
- **First-match constructor selection** (Dapper chooses by column/parameter type match). More flexible but order- and
  data-dependent. Rejected for the widest-constructor rule; revisit with an explicit `[VelQueryConstructor]`.

## Open questions

- *Resolved 2026-10-10:* `ValueConverter` now converts midnight `DateTime` → `DateOnly`, sub-day `TimeSpan` →
  `TimeOnly` and one-character `string` → `char`, rejecting anything lossy. Still needs SQL Server integration tests
  for `date`/`time` columns. (Tech Lead, Gate B)
- *Resolved 2026-10-10:* the reflection mapper uses the generator's rule — the single widest public constructor,
  ties rejected with `VelQueryMappingException` (the generator reports VQ1003) — and its XML doc says so.
- Custom type handlers (§9.3 rule 7) are compile-time only: define their declaration form with the generator.
  (Compiler lead, Gate C)
- Should ordinal resolution be cached per reader shape (e.g. keyed by field-count + names) once benchmarks justify
  it? (Performance lead, after v0.5 benchmarks)
