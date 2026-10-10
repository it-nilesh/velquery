```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.1 (25F80) [Darwin 25.5.0]
Apple M5 Pro, 1 CPU, 15 logical and 15 physical cores
.NET SDK 10.0.201
  [Host]   : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  Categories=typed-params  

```
| Method               | Mean     | Error       | StdDev    | P95      | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------- |---------:|------------:|----------:|---------:|------:|--------:|-------:|----------:|------------:|
| raw-adonet           | 361.1 μs |   562.81 μs |  30.85 μs | 391.5 μs |  1.00 |    0.10 | 0.4883 |   7.64 KB |        1.00 |
| dapper               | 342.1 μs |    99.88 μs |   5.47 μs | 346.0 μs |  0.95 |    0.07 | 0.9766 |  10.44 KB |        1.37 |
| dapper-aot           | 436.9 μs | 1,857.29 μs | 101.80 μs | 535.4 μs |  1.22 |    0.26 | 0.9766 |   8.29 KB |        1.08 |
| velquery-descriptor  | 438.8 μs | 1,279.11 μs |  70.11 μs | 506.9 μs |  1.22 |    0.19 | 0.9766 |   8.98 KB |        1.18 |
| velquery-runtime-sql | 396.4 μs |   913.66 μs |  50.08 μs | 445.6 μs |  1.10 |    0.14 | 0.9766 |   9.77 KB |        1.28 |
