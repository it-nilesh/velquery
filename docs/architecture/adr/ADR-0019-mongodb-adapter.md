# ADR-0019: MongoDB adapter (phase 3 pilot)

- **Status:** Proposed
- **Date:** 2026-10-20
- **Implements:** ADR-0015 decision 4 (D-13), PRD amendment v1.1 VQ-DB-005/006/007; builds on ADR-0016

## Context

The first non-relational adapter must prove that VelQuery's guarantees — compile-time-checked typed contracts, strict
reflection-free mapping, explicit ownership, value-free errors, safe binding, a native bulk path and honest benchmarks —
carry over to a document database without depending on `VelQuery.Core` (ADO.NET).

## Decision

1. **Packages.** `VelQuery.MongoDB` (Abstractions + MongoDB.Driver 3.x + DI abstractions) and
   `VelQuery.MongoDB.Generators` (netstandard2.0 incremental generator). No dependency on Core.
2. **Contracts.** One attribute per operation (`[VelQueryMongoFind|Count|Aggregate|Update|Delete|Insert]`) on a partial
   class with one partial method; the return type selects cardinality (reusing `QueryCardinality`). Filters, updates,
   sorts, projections and pipelines are strict JSON + Extended JSON v2 wrappers + `@name` placeholders, parsed at build
   time and emitted as direct `BsonDocument` construction (no runtime JSON parsing). Diagnostics use VQ2001–VQ2017.
3. **Injection guarantee.** Placeholders are values only (VQ2006); parameters bind only as scalars or sequences of
   scalars (VQ2008); positions that are code, identifiers or query shape (`$where`, `$function`, sort, projection, …)
   reject placeholders (VQ2007); arguments in aggregation-expression contexts are wrapped in `$literal`.
4. **Mapping.** A generated `IBsonSerializer<T>` per document type: typed reads, byte-level field-name matching, strict
   lossless conversions, unknown fields skipped, duplicate/missing/NULL failures naming the field path. It reaches the
   driver per operation — rendered projection / pipeline output serializers for reads — never through the global
   serializer registry or class maps (tested).
5. **Ownership and errors.** Data sources own only clients they create; sessions are per-operation by default;
   `BeginTransactionAsync` owns a driver session and multi-document transaction (`IVelQueryTransactionScope`);
   application-owned `IClientSessionHandle`s are never committed, aborted or ended; `SessionGuard` rejects overlap.
   Errors go through `VelQueryErrors.Translate` with `MongoErrorClassifier`; driver exceptions stay inner (duplicate
   key 11000 inspectable).
6. **No implicit retries.** VelQuery-created clients disable retryable reads/writes and adaptive retries unless the
   connection string explicitly enables them. The driver-mandated single retry of commit/abort is documented.
7. **Writes as raw commands (revised 2026-10-21).** `insert`, `update` and `delete` are issued as server commands
   through `IMongoDatabase.RunCommandAsync` rather than the collection write API, which offers no server-side time
   limit and wraps caller-serialized documents twice. Every write carries `maxTimeMS` (the server enforces the
   operation timeout; the client deadline fires a grace period later as a backstop). Bulk inserts are framed by hand
   into the driver's pooled chunk buffers (`MultiChunkBuffer`/`BsonChunkPool`) with the generated serializer, split at
   16 MiB and 100,000 documents, with no per-document wrapper or write-model objects. Write and write-concern errors
   become `MongoWriteCommandException` (indexes across the whole sequence, codes; server text kept separately because
   it can contain key values). Sessions pass through, so transactions apply; nothing is retried.
8. **Bulk semantics.** Ordered. Outside a transaction partial success is possible (documented and tested); inside a
   transaction nothing commits on failure.

## Consequences

- 79 integration tests (mongo:8 single-node replica set) and 104 generator/diagnostic/parser tests pass.
- Reads are at parity with the raw driver (about −10% allocation on buffered/streamed reads).
- Bulk insert of 1M documents: 1.24 s, 0.45 s client CPU, 137 MB allocated, versus 1.89 s, 1.01 s and 505 MB for the
  driver's typed `InsertMany` (34% faster, 55% less CPU, 73% less allocation; local mongo:8).
- Writes are stopped by the server at their time limit (MaxTimeMSExpired → `VelQueryTimeoutException`); work completed
  before the limit (for example earlier batches outside a transaction) stays applied.
- Early stream exit kills the server cursor synchronously (`AsyncCursor.CloseAsync` is internal).
- MongoDB.Driver is not Native AOT compatible, whatever VelQuery's own annotations say.

## Alternatives considered

- **Per-collection serializer registry** — `MongoCollectionSettings.SerializerRegistry` has no public setter.
- **Global `BsonSerializer.RegisterSerializer`** — hidden mutable global state; rejected.
- **Serialize to `RawBsonDocument` before insert** — an extra byte copy per document instead of one small wrapper.
- **Runtime JSON parsing (`BsonDocument.Parse`)** — loses build-time checks and costs CPU per call.
- **LINQ/expression contracts** — contradicts the query-first principle.

## Open questions

- *Resolved 2026-10-21:* server-side write time limits (raw commands with `maxTimeMS`).
- Asynchronous cursor kill on early exit.
- Naming conventions (camelCase), more scalar types (float, short, DateTimeOffset, DateOnly), polymorphism,
  `IAsyncEnumerable<T>` bulk sources, placeholders inside `$lookup`/`$unionWith`/`$facet` sub-pipelines.
