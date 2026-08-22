using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Runtime;
using Polly;
using Polly.Registry;
using Polly.Retry;
using Polly.Timeout;

namespace Benchmarks.Placement;

[MemoryDiagnoser]
public class PlacementResilienceBenchmark
{
    private static readonly SiloAddress Result = SiloAddress.New(System.Net.IPAddress.Loopback, 11111, 1);
    private static readonly Func<SiloAddress, CancellationToken, ValueTask<SiloAddress>> PlacementCallback = ExecutePlacement;
    private static readonly Func<ResilienceContext, SiloAddress, ValueTask<Outcome<SiloAddress>>> PlacementOutcomeCallback = ExecutePlacementOutcome;
    private readonly CancellationTokenSource _shutdown = new();
    private ResiliencePipeline _emptyPipeline = null!;
    private ResiliencePipeline _legacyPipeline = null!;
    private ResiliencePipeline<SiloAddress> _pipeline = null!;
    private ServiceProvider _serviceProvider = null!;

    [Params(0, SiloMessagingOptions.DEFAULT_PLACEMENT_MAX_RETRIES)]
    public int MaxRetries { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        var services = new ServiceCollection();
        services.AddOptions<SiloMessagingOptions>().Configure(options => options.PlacementMaxRetries = MaxRetries);
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();
        OrleansRuntimeResiliencePolicies.AddOrleansRuntimeResiliencePolicies(services);
        services.AddResiliencePipeline("UntypedPlacement", builder =>
        {
            builder.AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = SiloMessagingOptions.DEFAULT_PLACEMENT_TIMEOUT,
            });

            if (MaxRetries > 0)
            {
                builder.AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = MaxRetries,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = SiloMessagingOptions.DEFAULT_PLACEMENT_RETRY_BASE_DELAY,
                    UseJitter = true,
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(),
                });
            }
        });
        _serviceProvider = services.BuildServiceProvider();
        _emptyPipeline = new ResiliencePipelineBuilder().Build();
        var pipelineProvider = _serviceProvider.GetRequiredService<ResiliencePipelineProvider<string>>();
        _legacyPipeline = pipelineProvider.GetPipeline("UntypedPlacement");
        _pipeline = pipelineProvider.GetPipeline<SiloAddress>(OrleansRuntimeResiliencePolicies.PlacementResiliencePipelineKey);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _shutdown.Dispose();
        _serviceProvider.Dispose();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<SiloAddress> DirectPlacement() => ExecutePlacement(Result, _shutdown.Token);

    [Benchmark]
    public ValueTask<SiloAddress> EmptyPipeline() =>
        _emptyPipeline.ExecuteAsync(PlacementCallback, Result, _shutdown.Token);

    [Benchmark]
    public ValueTask<SiloAddress> LegacyResilientPlacement() =>
        _legacyPipeline.ExecuteAsync(PlacementCallback, Result, _shutdown.Token);

    [Benchmark]
    public async ValueTask<SiloAddress> ResilientPlacement()
    {
        var context = ResilienceContextPool.Shared.Get(_shutdown.Token);
        try
        {
            var outcome = await _pipeline.ExecuteOutcomeAsync(PlacementOutcomeCallback, context, Result);
            outcome.ThrowIfException();
            return outcome.Result!;
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    [Benchmark]
    public bool ShutdownCheck() => _shutdown.IsCancellationRequested;

    private static ValueTask<SiloAddress> ExecutePlacement(SiloAddress result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(result);
    }

    private static ValueTask<Outcome<SiloAddress>> ExecutePlacementOutcome(ResilienceContext context, SiloAddress result)
    {
        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            return Outcome.FromResultAsValueTask(result);
        }
        catch (Exception exception)
        {
            return Outcome.FromExceptionAsValueTask<SiloAddress>(exception);
        }
    }
}
