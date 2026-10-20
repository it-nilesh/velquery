; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
VQ1001 | VelQuery.Contracts | Error | SQL references a parameter not declared by the contract method
VQ1002 | VelQuery.Contracts | Warning | Contract method parameter not referenced by the SQL
VQ1003 | VelQuery.Contracts | Error | Unsupported result row type, constructor or member type
VQ1004 | VelQuery.Contracts | Error | Two members map to the same column
VQ1012 | VelQuery.Contracts | Error | Contract type is not a non-generic partial class in partial containing types
VQ1013 | VelQuery.Contracts | Error | Contract does not declare exactly one partial method definition
VQ1014 | VelQuery.Contracts | Error | Invalid contract method signature
VQ1015 | VelQuery.Contracts | Error | Return type not supported for the cardinality
VQ1016 | VelQuery.Contracts | Error | Ambiguous operation; explicit Cardinality required
VQ1017 | VelQuery.Contracts | Error | Invalid [VelQuerySql] argument
VQ1018 | VelQuery.Contracts | Error | Parameter type has no inferable DbType
VQ1019 | VelQuery.Contracts | Warning | Unterminated literal, identifier or comment in SQL
VQ1020 | VelQuery.Contracts | Error | Two method parameters bind the same SQL parameter
VQ1021 | VelQuery.Contracts | Error | Internal generator failure
VQ1022 | VelQuery.Contracts | Warning | Placeholder style (?, ?NNN, :name, $name, $1) that VelQuery does not bind
VQ1023 | VelQuery.Contracts | Warning | Same placeholder spelled with different casing
VQ1024 | VelQuery.Contracts | Warning | Unknown SQL dialect name
