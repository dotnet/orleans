using System.Diagnostics;
using System.Text.Json;

namespace Benchmarks.Journaling.Azure;

internal static class AzureJournalRunner
{
    public static async Task<int> RunCommandAsync(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("""
                Journaling.Azure --backend AzuriteBlob|AzuriteTable|StandardBlob|PremiumBlob|Table
                  --workload DurableAppend|CheckpointReplace|RecoveryReplay|CatalogBounded|CatalogUnbounded
                  --operations 8 --concurrency 2 --payload-bytes 256 --batch-size 4 --history-batches 4
                  --checkpoint-bytes 4096 --due-journals 16 --future-journals 64 --metadata false --seed 42
                  --setup-timeout-seconds 120 --timeout-seconds 60 --cleanup-timeout-seconds 60
                  --max-work 100000 --max-bytes 268435456 --allow-azure false --output azure-journal-results
                Azure endpoints and emulator connection settings are environment-only. See Journaling\Azure\README.md.
                Each run creates an isolated resource and new .json/.csv files. Existing output files are preserved.
                """);
            return 0;
        }

        AzureJournalOptions options;
        try
        {
            options = AzureJournalOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Invalid benchmark configuration: {exception.Message} Use Journaling.Azure --help.");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            var report = await RunAsync(options, cancellation.Token);
            try
            {
                await report.ExportAsync(options.Output);
            }
            catch (Exception exception)
            {
                report.Failures.Add(BenchmarkFailure.From("export", exception));
                Console.WriteLine(JsonSerializer.Serialize(report, AzureJournalReport.JsonOptions));
                return 1;
            }

            Console.WriteLine(JsonSerializer.Serialize(report, AzureJournalReport.JsonOptions));
            return report.Success ? 0 : 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    internal static async Task<AzureJournalReport> RunAsync(
        AzureJournalOptions options,
        CancellationToken cancellationToken = default,
        Func<AzureJournalOptions, AzureJournalReport, IAzureJournalScenario>? createScenario = null)
    {
        options.Validate();
        var report = new AzureJournalReport(options);
        var scenario = createScenario is null ? new AzureJournalScenario(options, report) : createScenario(options, report);
        try
        {
            using (var setup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                setup.CancelAfter(TimeSpan.FromSeconds(options.SetupTimeoutSeconds));
                await scenario.PrepareAsync(setup.Token);
            }

            report.Phase = "measurement";
            using (var measurement = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                measurement.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                await MeasureAsync(scenario, options, report, measurement);
            }

            if (report.Failures.Count == 0 && report.Completed == options.Operations)
            {
                report.Phase = "verification";
                using var verification = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                verification.CancelAfter(TimeSpan.FromSeconds(options.SetupTimeoutSeconds));
                await scenario.VerifyAsync(report.Operations.Where(result => result.Outcome == "completed").Select(result => result.Index), verification.Token);
                report.Phase = "complete";
            }
        }
        catch (Exception exception)
        {
            report.Failures.Add(BenchmarkFailure.From(report.Phase, exception));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(options.CleanupTimeoutSeconds));
            try
            {
                // Every worker has joined before ownership cleanup, including after caller cancellation.
                await scenario.CleanupAsync(cleanup.Token);
            }
            catch (Exception exception)
            {
                report.Cleanup = "failed";
                report.Failures.Add(BenchmarkFailure.From("cleanup", exception));
            }
        }

        return report;
    }

    private static async Task MeasureAsync(
        IAzureJournalScenario scenario, AzureJournalOptions options, AzureJournalReport report, CancellationTokenSource cancellation)
    {
        var results = new OperationResult?[options.Operations];
        var failures = new BenchmarkFailure?[options.Operations];
        var next = -1;
        scenario.StartMetrics();
        var started = Stopwatch.GetTimestamp();
        try
        {
            // Allocate one task per worker, rather than one task per requested operation.
            var workers = new Task[options.Concurrency];
            for (var worker = 0; worker < workers.Length; worker++)
            {
                workers[worker] = WorkAsync();
            }

            await Task.WhenAll(workers);
        }
        finally
        {
            report.ElapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            report.ProviderMetrics = scenario.StopMetrics();
            report.Operations.AddRange(results.OfType<OperationResult>());
            report.Failures.AddRange(failures.OfType<BenchmarkFailure>());
            if (cancellation.IsCancellationRequested && report.Failures.Count == 0)
            {
                report.Failures.Add(BenchmarkFailure.From("measurement", new OperationCanceledException(cancellation.Token)));
            }
        }

        async Task WorkAsync()
        {
            while (!cancellation.IsCancellationRequested)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= options.Operations)
                {
                    return;
                }

                var timestamp = Stopwatch.GetTimestamp();
                try
                {
                    await scenario.ExecuteAsync(index, cancellation.Token);
                    results[index] = new(index, "completed", Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds,
                        options.PayloadBytesPerOperation, options.ItemsPerOperation);
                }
                catch (OperationCanceledException exception) when (cancellation.IsCancellationRequested)
                {
                    results[index] = new(index, "cancelled", Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds, 0, 0);
                    failures[index] = BenchmarkFailure.From("measurement", exception, index);
                }
                catch (Exception exception)
                {
                    results[index] = new(index, "failed", Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds, 0, 0);
                    failures[index] = BenchmarkFailure.From("measurement", exception, index);
                    await cancellation.CancelAsync();
                }
            }
        }
    }
}
