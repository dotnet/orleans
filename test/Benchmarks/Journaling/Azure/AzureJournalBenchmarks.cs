using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;

namespace Benchmarks.Journaling.Azure;

[BenchmarkCategory("Journaling.Azure")]
[Config(typeof(Configuration))]
[InvocationCount(1, unrollFactor: 1)]
[IterationCount(3)]
[WarmupCount(1)]
public class AzureJournalBenchmarks
{
    public sealed class Configuration : ManualConfig
    {
        public Configuration()
        {
            AddJob(Job.Default.WithStrategy(RunStrategy.Monitoring).WithWarmupCount(1).WithIterationCount(3).AsDefault());
            AddJob(Job.Default.DontEnforcePowerPlan().WithLaunchCount(1).AsMutator());
            AddValidator(new AzureJournalBenchmarkValidator());
        }
    }

    private AzureJournalScenario _scenario = null!;
    private AzureJournalReport _report = null!;
    private CancellationTokenSource _iteration = null!;
    private int _invocations;

    [ParamsSource(nameof(Backends))]
    public AzureJournalBackend Backend { get; set; }

    [ParamsSource(nameof(PayloadSizes))]
    public int PayloadBytes { get; set; }

    [ParamsAllValues]
    public AzureJournalWorkload Workload { get; set; }

    public IEnumerable<AzureJournalBackend> Backends => (Environment.GetEnvironmentVariable("JOURNAL_BENCHMARK_BDN_BACKENDS") ?? "AzuriteBlob,AzuriteTable")
        .Split(',').Select(AzureJournalOptions.ParseEnum<AzureJournalBackend>).Distinct().Take(6).ToArray() is { Length: <= 5 } backends
            ? backends : throw new ArgumentException("Select at most five backends.");

    public IEnumerable<int> PayloadSizes => (Environment.GetEnvironmentVariable("JOURNAL_BENCHMARK_BDN_PAYLOAD_BYTES") ?? "256")
        .Split(',').Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).Distinct().Take(4).ToArray() is { Length: <= 3 } sizes
            ? sizes : throw new ArgumentException("Select at most three payload sizes.");

    [GlobalSetup]
    public void ReportBuild() => BenchmarkBuildInfo.WriteTo(Console.Out);

    [IterationSetup]
    public void Setup()
    {
        var options = AzureJournalOptions.Parse((Environment.GetEnvironmentVariable("JOURNAL_BENCHMARK_BDN_OPTIONS") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)) with
        {
            Backend = Backend,
            PayloadBytes = PayloadBytes,
            Workload = Workload,
            Operations = 1,
            Concurrency = 1,
            AllowAzure = Environment.GetEnvironmentVariable("JOURNAL_BENCHMARK_ALLOW_AZURE") == "true"
        };
        options.Validate();
        _report = new AzureJournalReport(options);
        _scenario = new AzureJournalScenario(options, _report);
        _invocations = 0;
        try
        {
            using var setup = new CancellationTokenSource(TimeSpan.FromSeconds(options.SetupTimeoutSeconds));
            _scenario.PrepareAsync(setup.Token).GetAwaiter().GetResult();
            _iteration = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        }
        catch (Exception exception)
        {
            try
            {
                Cleanup();
            }
            finally
            {
                Console.Error.WriteLine($"Azure benchmark setup failed: {exception.GetType().Name}.");
            }

            throw new InvalidOperationException("Azure benchmark setup failed. See the sanitized phase and resource output.");
        }
    }

    [Benchmark]
    public async Task<long> Execute()
    {
        if (Interlocked.Increment(ref _invocations) != 1)
        {
            throw new InvalidOperationException("Azure benchmarks require exactly one invocation per iteration.");
        }

        try
        {
            await _scenario.ExecuteAsync(0, _iteration.Token);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Azure benchmark measurement failed: {exception.GetType().Name}.");
            throw new InvalidOperationException("Azure benchmark measurement failed. See the sanitized phase and resource output.");
        }

        return _report.Configuration.ItemsPerOperation;
    }

    [IterationCleanup]
    public void Cleanup()
    {
        try
        {
            if (_invocations == 1)
            {
                using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(_report.Configuration.SetupTimeoutSeconds));
                try
                {
                    _scenario.VerifyAsync([0], verification.Token).GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"Azure benchmark verification failed: {exception.GetType().Name}.");
                    throw new InvalidOperationException("Azure benchmark verification failed.");
                }
            }
        }
        finally
        {
            _iteration?.Dispose();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(_report.Configuration.CleanupTimeoutSeconds));
            try
            {
                _scenario.CleanupAsync(cleanup.Token).GetAwaiter().GetResult();
                if (_report.Failures.Count > 0)
                {
                    throw new InvalidOperationException("Azure benchmark lifecycle cleanup failed.");
                }
            }
            catch (Exception exception)
            {
                _report.Cleanup = "failed";
                Console.Error.WriteLine($"Azure benchmark cleanup failed: {exception.GetType().Name}.");
                throw new InvalidOperationException("Azure benchmark cleanup failed. Inspect the owned resource identified below.");
            }
            finally
            {
                Console.WriteLine($"Azure benchmark resource={_report.Resource}; ownership={_report.Ownership}; cleanup={_report.Cleanup}; account={_report.AccountVerification}/{_report.AccountKind}/{_report.AccountSku}");
            }
        }
    }
}
