```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.1 (25F80) [Darwin 25.5.0]
Apple M5 Pro, 1 CPU, 15 logical and 15 physical cores
.NET SDK 10.0.201
  [Host]   : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=5  LaunchCount=1  
WarmupCount=2  Categories=stream-100k  

```
| Method               | Mean     | Error     | StdDev    | P95      | Ratio | RatioSD | Gen0      | Gen1    | Allocated | Alloc Ratio |
|--------------------- |---------:|----------:|----------:|---------:|------:|--------:|----------:|--------:|----------:|------------:|
| raw-adonet           | 41.26 ms |  9.284 ms |  2.411 ms | 44.07 ms |  1.00 |    0.07 | 2700.0000 |       - |   21.7 MB |        1.00 |
| dapper               | 41.80 ms |  7.392 ms |  1.144 ms | 43.04 ms |  1.02 |    0.06 | 3666.6667 | 83.3333 |  29.31 MB |        1.35 |
| dapper-aot           | 57.67 ms | 40.959 ms | 10.637 ms | 70.99 ms |  1.40 |    0.25 | 2625.0000 |       - |  21.69 MB |        1.00 |
| velquery-descriptor  | 53.14 ms |  1.315 ms |  0.341 ms | 53.57 ms |  1.29 |    0.07 | 4583.3333 | 83.3333 |  36.64 MB |        1.69 |
| velquery-runtime-sql | 49.59 ms | 25.587 ms |  6.645 ms | 54.44 ms |  1.21 |    0.16 | 4642.8571 | 71.4286 |  37.27 MB |        1.72 |
