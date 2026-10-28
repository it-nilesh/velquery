```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.1 (25F80) [Darwin 25.5.0]
Apple M5 Pro, 1 CPU, 15 logical and 15 physical cores
.NET SDK 10.0.201
  [Host]   : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=5  LaunchCount=1  
WarmupCount=2  Categories=stream-100k  

```
| Method               | Mean     | Error     | StdDev   | P95      | Ratio | RatioSD | Gen0      | Gen1     | Allocated | Alloc Ratio |
|--------------------- |---------:|----------:|---------:|---------:|------:|--------:|----------:|---------:|----------:|------------:|
| raw-adonet           | 37.03 ms |  1.185 ms | 0.183 ms | 37.24 ms |  1.00 |    0.01 | 2714.2857 |        - |  21.64 MB |        1.00 |
| dapper               | 37.76 ms |  1.098 ms | 0.170 ms | 37.87 ms |  1.02 |    0.01 | 3642.8571 |  71.4286 |  29.32 MB |        1.35 |
| dapper-aot           | 45.00 ms | 23.825 ms | 6.187 ms | 50.44 ms |  1.22 |    0.15 | 2700.0000 |        - |   21.7 MB |        1.00 |
| velquery-descriptor  | 45.69 ms | 27.330 ms | 7.097 ms | 51.36 ms |  1.23 |    0.18 | 2714.2857 |        - |  21.68 MB |        1.00 |
| velquery-runtime-sql | 51.72 ms |  3.025 ms | 0.786 ms | 52.79 ms |  1.40 |    0.02 | 2714.2857 |        - |  21.64 MB |        1.00 |
| efcore-compiled      | 45.71 ms | 26.424 ms | 6.862 ms | 51.27 ms |  1.23 |    0.17 | 6900.0000 | 200.0000 |  55.38 MB |        2.56 |
