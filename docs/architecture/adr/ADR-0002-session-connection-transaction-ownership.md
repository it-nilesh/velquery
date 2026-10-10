# ADR-0002: Session, connection and transaction ownership; streaming disposal; error contract

- **Status:** Proposed
- **Date:** 2026-10-10
- **Deciders:** Principal Architect, Tech Lead
- **PRD references:** D-01, D-08, VQ-FR-001, -009..-013, -021, -026, -027, §6.1, §8.2, §9.1, §9.6, §13
- **Code:** `src/VelQuery.Core/Api/VelQueryDataSource.cs`, `Execution/VelQuerySession.cs`,
  `Execution/VelQuerySession.Execution.cs`, `Execution/QueryStream.cs`, `Api/VelQueryTransaction.cs`,
  `Api/VelQueryMultipleReader.cs`, `Api/VelQueryOptions.cs`, `Abstractions/Errors/VelQueryException.cs`

## Context

Resource safety outranks micro-optimisation (§13.1): every acquired connection, command and reader must be released
exactly once on success, failure, cancellation and early enumeration exit, and VelQuery must never touch resources
it does not own. The PRD §8 sample used `await factory.OpenSessionAsync(ct)`, i.e. a session that opens a connection
up front and holds it for its lifetime. In a DI-scoped session (one per HTTP request) that pins a pooled connection
for the whole request, including time spent in non-database code, and makes session creation perform I/O.

## Decision

**Entry point.** `VelQueryDataSource` (thread-safe, one per database) combines an `IVelQueryProvider`, an optional
`IVelQueryConnectionFactory` and options. Sessions are cheap, non-thread-safe objects (D-01).

**Ownership modes.**

| Mode | Created by | Connection | Transaction |
|---|---|---|---|
| Factory-owned | `dataSource.CreateSession()` — no I/O | acquired from the factory (driver pool) **per operation** and disposed when the operation ends | none, unless `BeginTransactionAsync` |
| Session transaction | `await session.BeginTransactionAsync(iso, ct)` | opens one connection and **pins** it until the returned `VelQueryTransaction` is disposed | VelQuery-owned; `CommitAsync` is explicit; disposing without commit rolls back (via `DbTransaction.DisposeAsync`) and releases the pinned connection |
| Caller-owned | `dataSource.CreateSession(connection, transaction?)` | must already be `Open` (validated); never opened, closed or disposed by VelQuery | must belong to that connection (validated); never committed or rolled back; `BeginTransactionAsync` is rejected |

`OpenSessionAsync` was replaced by `CreateSession()` because per-operation acquisition matches ADO.NET pooling
practice, keeps pool hold time to the operation, satisfies §6.1 ("opened only when needed and closed after
materialization"), and keeps session construction free of I/O and failure. Pinning happens only when correctness
needs one physical connection (transactions, caller connections). `IVelQueryConnectionFactory` exposes only
`CreateOpenConnectionAsync`; `DataSourceConnectionFactory` wraps an application-owned `DbDataSource`. VelQuery never
pools connections itself.

**Concurrency.** Sessions are not thread-safe. On a pinned connection an interlocked busy flag rejects a second
operation (including a still-open stream or multi-result reader) with `VelQueryValidationException`, instead of
letting the driver fail obscurely. Multi-result readers have their own single-consumer guard.

**Streaming (D-08).** `StreamAsync` returns a lazy `IAsyncEnumerable<T>`; each enumeration executes once. The
enumerator owns the connection lease, command and reader. On normal completion it drains remaining result sets (so
errors from later statements surface), disposes the reader, then captures output parameters. On early exit (`break`,
consumer exception, cancellation) `DisposeAsync` treats the enumeration as faulted and calls `DbCommand.Cancel()`
before disposing the reader so the driver does not drain the remaining rows from the server. The call-site token and
the `WithCancellation` token are linked. Buffered operations drain trailing result sets; `QueryFirst*` uses
`CommandBehavior.SingleRow` and does not. `VelQueryMultipleReader` enforces in-order consumption and cancels on
early dispose.

**Cleanup.** `CleanupAsync` releases reader → command → lease in order. When the operation already failed,
cleanup exceptions are swallowed so they can never mask the original exception; when the operation succeeded, the
first cleanup failure is rethrown.

**Error contract (VQ-FR-026).** All VelQuery errors derive from `VelQueryException` with a `Category` and `QueryId`.

- Caller cancellation (token signalled) surfaces as `OperationCanceledException` (driver exception as inner when the
  driver threw something else). Provider-classified cancellation also maps to `OperationCanceledException`.
- Provider timeout (`SqlException.Number == -2`) → `VelQueryTimeoutException`; any other `DbException` →
  `VelQueryProviderException`. The original exception is always `InnerException`; its message is **not** copied,
  because provider messages can echo data. VelQuery messages carry query ID, column and type names only — never
  parameter values, row values or connection strings.
- `VelQueryException`s (mapping, no-rows, too-many-rows, validation, unsupported capability) and non-`DbException`
  failures pass through with their original stack (`ExceptionDispatchInfo`).
- Commit/rollback/begin failures → `VelQueryTransactionException`; a failed commit states that the database decides
  whether changes were applied. Nothing is retried (§13.1).

**Timeouts (VQ-FR-010).** Precedence: per call (`session.Sql(...).WithTimeout(t)`, or a descriptor's `CommandTimeout`, overridable for one
call with `descriptor.WithCommandTimeout(t)`) >
`session.CommandTimeout` > `VelQueryOptions.DefaultTimeout` > provider default (SqlClient: 30 s). Zero/negative
values are rejected; `Timeout.InfiniteTimeSpan` maps to ADO.NET `0`; partial seconds round up. Timeouts are the
provider's per-command timeout, not an end-to-end deadline (§8.2).

**Options.** `VelQueryOptions` is a mutable class (`DefaultTimeout`, `StrictMapping`, `AllowDynamicFallback`) that is
validated and snapshotted into an internal immutable `ResolvedOptions` when the data source is created; later
mutation has no effect. The PRD's `readonly record struct VelQueryOptions(...)` plus `services.AddVelQuery(options =>
{ options.DefaultTimeout = ...; })` could not compile: positional readonly struct properties are not assignable, and
an `Action<T>` over a struct would mutate a copy anyway.

## Consequences

- Pool hold time is bounded by each operation; a session can be injected per request at no cost.
- A sequence of operations on a factory-owned session may use different physical connections: session state
  (`SET` options, temp tables, `sp_set_session_context`) does not carry over. Users needing that must use a session
  transaction or a caller-owned connection. This must be prominent in docs and the Dapper migration guide.
- Early `break` in `await foreach` is cheap for large results, but the server sees an attention/cancel. Whether a
  pinned connection's transaction is still usable afterwards is provider-specific and needs integration tests.
- Wrapping provider exceptions gives one portable taxonomy and redaction by default, at a migration cost: Dapper code
  such as `catch (SqlException ex) when (ex.Number == 2627)` becomes
  `catch (VelQueryProviderException ex) when (ex.InnerException is SqlException { Number: 2627 })`. Provider-specific
  helpers (e.g. an error-number accessor) may be added later without changing the contract.
- `AddVelQuerySqlServer` registers `IVelQuerySession` as scoped. Sessions implement both `IAsyncDisposable` and
  `IDisposable` so synchronously disposed DI scopes work; synchronous disposal gives the same rollback/release
  guarantees but blocks on driver I/O, so `await using`/`CreateAsyncScope()` remain preferred.
- `VelQuerySqlServer.CreateDataSource` creates the SqlClient `DbDataSource` and the returned `VelQueryDataSource` owns
  it (disposing one disposes the other). A `DataSourceConnectionFactory` built over an application-supplied data source
  does not dispose it unless constructed with `ownsDataSource: true`.

## Alternatives considered

- **Session opens a connection up front (`OpenSessionAsync`)** — PRD sketch. Holds pool slots for the session
  lifetime and makes construction fallible. Rejected; available explicitly via a caller-owned connection or a
  session transaction.
- **Let `DbException` propagate unwrapped (Dapper behaviour).** Zero-friction migration and familiar catch blocks,
  but no provider-neutral categories, no guarantee that messages are free of values, and timeout vs. cancellation vs.
  provider failure are indistinguishable without provider knowledge. Rejected; revisit if design partners find the
  migration cost blocking.
- **Drain remaining rows on early stream exit** instead of cancelling. Lets the connection be reused without an
  attention round trip, but reading the rest of a million-row result to throw it away defeats streaming. Rejected.
- **Thread-safe sessions via locking.** Hides concurrency bugs and serialises unrelated work (§6.1 "no global lock
  workaround"). Rejected in favour of fail-fast detection on pinned connections.

## Open questions

- Factory-owned sessions have no busy guard because each operation gets its own connection, so concurrent use
  currently works by accident. Document as unsupported, or guard it too? (Tech Lead, Gate B)
- `session.DisposeAsync()` with an un-disposed stream on a pinned connection: define and test the order (dispose the
  stream first, or fail?). (Tech Lead, Gate B)
- `VelQuerySqlServer.CreateDataSource` creates a SqlClient `DbDataSource` that `VelQueryDataSource` never disposes
  (it is not disposable). Acceptable for a process-lifetime singleton; confirm or make `VelQueryDataSource`
  `IAsyncDisposable`. (Architect, Gate A)
- Should provider-reported deadlocks/transient errors get their own category (still no retry)? (Provider lead, Gate C)
