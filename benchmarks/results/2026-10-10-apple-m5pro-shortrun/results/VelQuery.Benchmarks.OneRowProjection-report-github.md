```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.1 (25F80) [Darwin 25.5.0]
Apple M5 Pro, 1 CPU, 15 logical and 15 physical cores
.NET SDK 10.0.201
  [Host]   : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  Categories=one-row-projection  

```
| Method               | Mean     | Error    | StdDev   | P95      | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------- |---------:|---------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| raw-adonet           | 349.0 μs | 115.5 μs |  6.33 μs | 355.3 μs |  1.00 |    0.02 | 0.4883 |    6.1 KB |        1.00 |
| dapper               | 369.7 μs | 649.9 μs | 35.62 μs | 403.6 μs |  1.06 |    0.09 | 0.4883 |   7.86 KB |        1.29 |
| dapper-aot           | 365.1 μs | 132.7 μs |  7.28 μs | 370.4 μs |  1.05 |    0.02 | 0.4883 |   6.74 KB |        1.10 |
| velquery-descriptor  | 347.9 μs | 159.2 μs |  8.73 μs | 354.3 μs |  1.00 |    0.03 | 0.4883 |   7.29 KB |        1.19 |
| velquery-runtime-sql | 355.5 μs | 119.6 μs |  6.55 μs | 362.0 μs |  1.02 |    0.02 | 0.4883 |   7.62 KB |        1.25 |
