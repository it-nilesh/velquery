```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.1 (25F80) [Darwin 25.5.0]
Apple M5 Pro, 1 CPU, 15 logical and 15 physical cores
.NET SDK 10.0.201
  [Host]   : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  Categories=scalar-count  

```
| Method               | Mean     | Error     | StdDev   | P95      | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------- |---------:|----------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| raw-adonet           | 400.5 μs |  66.06 μs |  3.62 μs | 403.4 μs |  1.00 |    0.01 | 0.4883 |   4.12 KB |        1.00 |
| dapper               | 397.6 μs | 219.99 μs | 12.06 μs | 409.5 μs |  0.99 |    0.03 | 0.4883 |   4.27 KB |        1.04 |
| dapper-aot           | 398.7 μs |  83.03 μs |  4.55 μs | 403.1 μs |  1.00 |    0.01 | 0.4883 |   4.38 KB |        1.06 |
| velquery-descriptor  | 411.8 μs | 160.17 μs |  8.78 μs | 420.5 μs |  1.03 |    0.02 | 0.4883 |   4.47 KB |        1.09 |
| velquery-runtime-sql | 399.0 μs |  25.09 μs |  1.38 μs | 400.0 μs |  1.00 |    0.01 | 0.4883 |    4.5 KB |        1.09 |
| efcore-compiled      | 406.4 μs | 240.38 μs | 13.18 μs | 418.6 μs |  1.01 |    0.03 | 0.4883 |   5.73 KB |        1.39 |
