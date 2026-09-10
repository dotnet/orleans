# Cron benchmarks

Run the full matrix in Release mode:

```sh
dotnet run --project test/Benchmarks/Benchmarks.csproj -c Release -f net10.0 -- suite --filter '*ReminderCron*' --job Short --exporters JSON GitHub
```

Use `-f net8.0` to measure .NET 8. Each class uses `MemoryDiagnoser`, so reports include managed bytes allocated per operation alongside execution time.

| Class | Workloads |
| --- | --- |
| `ReminderCronBenchmarks` | Parsing, first and warmed queries, and enumeration for frequent, daily, leap-day, and impossible schedules. |
| `ReminderCronParsingBenchmarks` | Five and six fields, macros, named values, wrapping ranges, lists, steps, and calendar selectors. |
| `ReminderCronInvalidInputBenchmarks` | Empty input, unknown macros, missing fields, invalid selectors, and numeric overflow. |
| `ReminderCronCalendarBenchmarks` | Gregorian century boundaries, sparse day/weekday intersections, impossible dates, nearest weekdays, and ordinal weekdays. |
| `ReminderCronEnumerationBenchmarks` | Streaming versus materializing 10, 100, and 1,000 occurrences in UTC and a seasonal time zone. |
| `ReminderCronTimeZoneBenchmarks` | Paris, Vienna, Kyiv, Sydney, Lord Howe, Dubai, Kathmandu, Chatham, Casablanca, Apia, and New York: repeated clocks, fractional offsets, Ramadan changes, a skipped date, and cache eviction across 4,096 dates. |
| `ReminderCronDateTimeOffsetBenchmarks` | Offset-aware next-occurrence and enumeration queries, including fractional offsets and clock rollback. |

`ParseAndSearch` creates a new parsed schedule for every operation, including impossible-date searches. Warmed queries reuse the parsed schedule and time zone mappings. `UncachedDate` rotates through more dates than the mapping cache can retain. Enumeration benchmarks return or consume their results so the measured work includes evaluating the requested occurrences.

Correctness, independent calendar and time zone oracles, and allocation regression checks live in `test/Orleans.Core.Tests/AdvancedReminders/ReminderCron*Tests.cs`. Benchmark results measure performance without imposing machine-dependent time limits on those tests.
