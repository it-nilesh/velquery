```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.1 (25F80) [Darwin 25.5.0]
Apple M5 Pro, 1 CPU, 15 logical and 15 physical cores
.NET SDK 10.0.201
  [Host]   : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  Categories=scalar-count  

```
| Method               | Mean     | Error       | StdDev   | P95      | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------- |---------:|------------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| raw-adonet           | 410.1 μs |    87.63 μs |  4.80 μs | 414.7 μs |  1.00 |    0.01 | 0.4883 |   4.12 KB |        1.00 |
| dapper               | 433.5 μs | 1,064.88 μs | 58.37 μs | 490.9 μs |  1.06 |    0.12 | 0.4883 |   4.27 KB |        1.04 |
| dapper-aot           | 405.1 μs |   119.48 μs |  6.55 μs | 409.6 μs |  0.99 |    0.02 | 0.4883 |   4.38 KB |        1.06 |
| velquery-descriptor  | 422.7 μs |   301.12 μs | 16.51 μs | 435.0 μs |  1.03 |    0.04 | 0.4883 |   4.41 KB |        1.07 |
| velquery-runtime-sql | 440.0 μs |   890.83 μs | 48.83 μs | 488.1 μs |  1.07 |    0.10 | 0.4883 |   4.45 KB |        1.08 |
