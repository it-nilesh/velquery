```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.1 (25F80) [Darwin 25.5.0]
Apple M5 Pro, 1 CPU, 15 logical and 15 physical cores
.NET SDK 10.0.201
  [Host]   : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  Categories=one-row-projection  

```
| Method               | Mean     | Error     | StdDev   | P95      | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------- |---------:|----------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| raw-adonet           | 359.6 μs | 151.53 μs |  8.31 μs | 367.7 μs |  1.00 |    0.03 | 0.4883 |    6.1 KB |        1.00 |
| dapper               | 354.9 μs | 110.56 μs |  6.06 μs | 360.8 μs |  0.99 |    0.02 | 0.4883 |   7.86 KB |        1.29 |
| dapper-aot           | 357.8 μs | 222.37 μs | 12.19 μs | 369.9 μs |  1.00 |    0.04 | 0.4883 |   6.75 KB |        1.11 |
| velquery-descriptor  | 355.8 μs | 137.42 μs |  7.53 μs | 363.0 μs |  0.99 |    0.03 | 0.4883 |   6.81 KB |        1.12 |
| velquery-runtime-sql | 354.0 μs |  36.37 μs |  1.99 μs | 355.9 μs |  0.98 |    0.02 | 0.4883 |   7.14 KB |        1.17 |
| efcore-compiled      | 355.5 μs | 100.25 μs |  5.50 μs | 360.7 μs |  0.99 |    0.02 | 0.9766 |   8.81 KB |        1.44 |
