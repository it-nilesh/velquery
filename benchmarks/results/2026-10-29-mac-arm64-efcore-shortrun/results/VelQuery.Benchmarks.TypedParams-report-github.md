```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.1 (25F80) [Darwin 25.5.0]
Apple M5 Pro, 1 CPU, 15 logical and 15 physical cores
.NET SDK 10.0.201
  [Host]   : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  Categories=typed-params  

```
| Method               | Mean     | Error     | StdDev  | P95      | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------- |---------:|----------:|--------:|---------:|------:|--------:|-------:|----------:|------------:|
| raw-adonet           | 343.0 μs | 135.02 μs | 7.40 μs | 350.3 μs |  1.00 |    0.03 | 0.4883 |   7.64 KB |        1.00 |
| dapper               | 346.6 μs |   5.37 μs | 0.29 μs | 346.9 μs |  1.01 |    0.02 | 0.9766 |  10.44 KB |        1.37 |
| dapper-aot           | 348.2 μs |  73.41 μs | 4.02 μs | 352.0 μs |  1.02 |    0.02 | 0.9766 |   8.29 KB |        1.08 |
| velquery-descriptor  | 347.9 μs | 116.87 μs | 6.41 μs | 353.7 μs |  1.01 |    0.02 | 0.9766 |   8.54 KB |        1.12 |
| velquery-runtime-sql | 348.7 μs |  48.31 μs | 2.65 μs | 350.4 μs |  1.02 |    0.02 | 0.9766 |    9.3 KB |        1.22 |
| efcore-compiled      | 359.7 μs |  53.49 μs | 2.93 μs | 362.3 μs |  1.05 |    0.02 | 0.9766 |  10.91 KB |        1.43 |
