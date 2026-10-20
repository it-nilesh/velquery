# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Report them privately through the repository's
**Security → Report a vulnerability** form (GitHub private vulnerability reporting). Include affected packages and
versions, a minimal reproduction, and the impact you observed. You will receive an acknowledgement, and fixes are
coordinated before public disclosure.

## Supported versions

VelQuery is pre-release (`0.x`). Only the latest `main` and the most recent published prerelease receive fixes.

## What VelQuery guarantees, and how it is tested

| Guarantee | Enforced by |
|---|---|
| Parameter values are always bound, never concatenated into SQL | Binders/`ParameterWriter`; VQ1008 rejects interpolated or concatenated SQL; injection tests per provider |
| MongoDB placeholders can only be values, never operators or code | VQ2006–VQ2008 at build time; `$literal` wrapping; operator-injection integration tests |
| Exceptions and diagnostics never contain parameter values, row values or connection strings | Error types carry query ID, column and types only; canary-value tests |
| Bulk loads never silently drop rows | MySQL warning/row-count checks; atomic defaults on every provider |
| Dependencies have no known vulnerabilities | NuGet audit runs on every build (warnings are errors); CI fails on vulnerable packages; Dependabot |

Known driver-level limits (for example Native AOT support in Microsoft.Data.SqlClient) are listed in
[docs/provider-compatibility.md](docs/provider-compatibility.md).
