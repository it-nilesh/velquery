; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
VQ1001 | VelQuery.Contracts | Error | SQL references a parameter not declared by the contract method
VQ1002 | VelQuery.Contracts | Warning | Contract method parameter not referenced by the SQL
VQ1003 | VelQuery.Contracts | Error | Unsupported result row type, constructor or member type
VQ1004 | VelQuery.Contracts | Error | Two members map to the same column
VQ1005 | VelQuery.Contracts | Error | Table or column not in the schema snapshot
VQ1011 | VelQuery.Contracts | Info | Query not fully checked against the schema snapshot
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
VQ1025 | VelQuery.Contracts | Error | Query does not return a column the result type requires
VQ1026 | VelQuery.Contracts | Warning | Nullable column read into a non-nullable member
VQ1027 | VelQuery.Contracts | Warning | Column type cannot be converted to the member type
VQ1028 | VelQuery.Contracts | Error | Query returns the same column name more than once
VQ1029 | VelQuery.Contracts | Warning | INSERT does not set a NOT NULL column without default
VQ1030 | VelQuery.Contracts | Warning | SQL writes a database-generated column
VQ1031 | VelQuery.Contracts | Warning | Schema snapshot cannot be used
