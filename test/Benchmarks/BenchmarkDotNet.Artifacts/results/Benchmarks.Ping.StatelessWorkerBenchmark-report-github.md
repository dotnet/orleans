```

BenchmarkDotNet v0.13.12, Windows 11 (10.0.22000.2538/21H2/SunValley)
Intel Core i7-10510U CPU 1.80GHz, 1 CPU, 8 logical and 4 physical cores
.NET SDK 9.0.100
  [Host]   : .NET 8.0.11 (8.0.1124.51707), X64 RyuJIT AVX2
  ShortRun : .NET 8.0.11 (8.0.1124.51707), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method    | Mean    | Error    | StdDev   | Max Workers | Average Workers | Allocated |
|---------- |--------:|---------:|---------:|------------:|----------------:|----------:|
| Monotonic | 5.058 s | 0.1071 s | 0.0059 s |           0 |            0.00 | 321.89 KB |
| Adaptive  | 5.214 s | 5.2937 s | 0.2902 s |           0 |            0.00 |  320.6 KB |
