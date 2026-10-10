# Product queries

One `[VelQuerySql]` contract per file. Each is static SQL checked at build time: a misspelled `@parameter` is error
VQ1001 and an unused method parameter is warning VQ1002. The generator implements every partial method.

Contracts are `internal`; application code uses `IProductRepository`, which can be faked in tests.
