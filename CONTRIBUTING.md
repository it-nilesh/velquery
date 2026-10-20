# Contributing to VelQuery

Thanks for helping. VelQuery favors correctness and honest evidence over features; please read this first.

## Ground rules

- Conventions, layout and the non-negotiable runtime rules are in [AGENTS.md](AGENTS.md). They apply to human and AI
  contributors alike.
- Requirements live in [docs/prd/](docs/prd/); significant design decisions get an ADR in
  [docs/architecture/adr/](docs/architecture/adr/) (copy the template in its README).
- No performance claim without a reproducible benchmark, losses included ([benchmarks/README.md](benchmarks/README.md)).

## Build and test

```bash
dotnet build VelQuery.slnx              # warnings are errors, including dead code (unused usings/members)
dotnet test --solution VelQuery.slnx    # integration suites need Docker; SQLite needs none
dotnet publish tests/VelQuery.NativeAot.Smoke -c Release -r <rid>   # Native AOT smoke
```

Package versions are central (`Directory.Packages.props`) with lock files: after changing packages run
`dotnet restore VelQuery.slnx --force-evaluate` and commit the updated `packages.lock.json` files.

## Definition of done for a pull request

- Acceptance tests are automated and pass, including lifecycle, cancellation and failure paths.
- No dead code: the build enforces IDE0005/IDE0051/IDE0052/IDE0059/CA1823; remove code instead of suppressing.
- Public API changes have XML documentation and a [CHANGELOG.md](CHANGELOG.md) entry.
- Provider changes update [docs/provider-compatibility.md](docs/provider-compatibility.md) and
  [docs/database-support.md](docs/database-support.md) — "verified" only when an automated test passed.
- No new hot-path allocations without benchmark evidence.
- Security-sensitive changes (binding, identifiers, error messages, bulk paths) get a second reviewer.
