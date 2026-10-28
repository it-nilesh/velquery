# ADR-0006: Static SQL subset and offline schema snapshots

- **Status:** Proposed
- **Date:** 2026-10-24
- **PRD references:** G-02, VQ-FR-034–039, §7.1 (VQ1005, VQ1011), §11.2, §11.3, §17.1
- **Code:** `src/VelQuery.Generators/Schema/`, `src/VelQuery.Generators/Sql/SqlTokenizer.cs`, `src/VelQuery.Cli/`

## Context

The VQ10xx checks so far compare SQL placeholders with method parameters. The more common production failures are
names and shapes: a misspelled or renamed column, a missing required column in the result, NULL read into a
non-nullable member. Dapper and Dapper.AOT find these at run time. Checking them at build time needs the schema, but
the build must never contact a database (VQ-FR-035), and a false error would block builds and destroy trust faster
than a missed one.

## Decision

1. **Snapshot file.** `velquery schema snapshot` (new `VelQuery.Cli` dotnet tool) exports tables, views, columns,
   types, nullability, defaults and generated flags into a canonical `velquery.schema.v1` JSON file: sorted, LF, no
   timestamps, with `snapshotSha256` over the content. Readers query catalog views only, in read-only sessions where
   the engine supports it. Connection strings come from an environment variable and are never printed.
2. **Build input.** `*.vqschema.json` files in the project directory are `AdditionalFiles` (from
   `VelQuery.Core.props`; opt out with `EnableVelQuerySchemaSnapshots=false`). One snapshot per project; its dialect
   must match the contract's.
3. **Shape, then match.** Per contract the generator tokenizes the SQL (same quoting rules as the placeholder
   scanner) and builds a `QueryShape`: query blocks, their sources, column references and outputs. This happens in
   the contract transform, so it is cached with the contract. A separate pipeline step matches shapes against the
   snapshot and only reports diagnostics; snapshot edits never regenerate code (tested).
4. **Report only what is proven.** Unknown constructs make the affected block opaque or its source unknown, which
   disables checks that depend on them instead of guessing. Unqualified names are checked only when every source in
   scope is known; qualified names only when the qualifier is a known alias. Because keyword lists can never be
   complete (`UTC_TIMESTAMP`, `GLOB`, `SOUNDS LIKE`), an unknown unqualified, unquoted name is reported only when it
   is a written column or looks like a misspelling of a real one ("did you mean"); qualified and quoted names are
   always reported. Tables the batch creates itself (`CREATE [TEMP] TABLE`, `SELECT ... INTO`) are unknown sources.
   The recognizer runs inside the schema step, only when a snapshot exists, and any internal failure becomes a VQ1011
   note for that query, never a generator failure. Types are compared with the runtime's
   lossless conversion rules (`ValueConverter`), and only combinations that fail for every row are reported.
5. **Severities.** Names that do not exist (VQ1005), required members with no column (VQ1025) and duplicate result
   names (VQ1028) are errors: each fails on every execution. Possible NULLs (VQ1026), impossible conversions (VQ1027)
   and write problems (VQ1029, VQ1030) are warnings. VQ1011 (info) says which part was not checked.
6. **Drift.** `velquery schema snapshot --check` and `velquery schema diff` compare the committed snapshot with a live
   database and classify changes as breaking or not (VQ-FR-039).

## Consequences

- Evidence: an end-to-end test per engine (SQL Server, PostgreSQL, MySQL, SQLite) exports a real schema, checks
  25 to 34 contracts with the real generator and then executes each one. Every contract the checker accepts runs,
  and every contract it rejects for a missing name, a required column or a generated column fails on the engine. All
  201 SQL statements in the existing integration suites were checked against their fixtures' snapshots: no false
  positives (the only findings were deliberate `no_such_table` queries).
- The snapshot can go stale; `--check` in CI is the mitigation. Static checks never claim full correctness.
- The recognizer is a maintained subset, not a SQL parser. New syntax first degrades to "not checked", never to a
  wrong error; extending it is incremental.
- Shape building runs once per contract and dialect inside the existing transform; matching is a dictionary lookup
  per name. No runtime cost: nothing is emitted into generated code.

## Alternatives considered

- **Connect to a database during the build** (as sqlx does by default): rejected by VQ-FR-035 and for CI
  reproducibility.
- **A full SQL parser per dialect** (for example a T-SQL ScriptDom dependency): far larger, one dialect at a time, and
  a heavy analyzer dependency; the subset covers the queries the corpus contains.
- **Executing `sp_describe_first_result_set` / `PREPARE` at build time:** exact, but needs a live database.

## Open questions

- Stored procedures: export routine signatures to check names and parameters (VQ1001-style) against the database.
- VQ1006 (string length/precision hints) can use the exported `maxLength`/`precision`/`scale`.
- Several snapshots per project (one per database) with a contract-level selector.
