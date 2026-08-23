using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace Benchmarks.Runtime;

[MemoryDiagnoser]
public class CallbackDataPoolingBenchmark
{
    private readonly Consumer _consumer = new();
    private ServiceProvider _serviceProvider = null!;
    private SharedCallbackData _shared = null!;
    private ApplicationRequestInstruments _instruments = null!;
    private IResponseCompletionSource _completion = null!;
    private Message _message = null!;
    private CancellationTokenSource _cancellation = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        _serviceProvider = services.BuildServiceProvider();
        _shared = new SharedCallbackData(
            unregister: _ => { },
            logger: NullLogger<CallbackData>.Instance,
            responseTimeout: TimeSpan.FromMinutes(1),
            cancelOnTimeout: false,
            waitForCancellationAcknowledgement: false,
            cancellationManager: null);
        _instruments = new(new OrleansInstruments(_serviceProvider.GetRequiredService<IMeterFactory>()));
        _completion = new NoOpResponseCompletionSource();
        _message = new Message();
        _cancellation = new CancellationTokenSource();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _cancellation.Dispose();
        _serviceProvider.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void AllocatedRequestLifecycle()
    {
        var callback = new CallbackData();
        callback.Initialize(_shared, _completion, _message, _instruments);
        callback.SubscribeForCancellation(_cancellation.Token);
        _consumer.Consume(callback);
        _consumer.Consume(callback);
        callback.Reset();
    }

    [Benchmark]
    public void PooledRequestLifecycle()
    {
        var owner = CallbackDataPool.Rent(_shared, _completion, _message, _instruments, out var senderLease);
        senderLease.Value.SubscribeForCancellation(_cancellation.Token);
        _consumer.Consume(senderLease.Value);
        senderLease.Dispose();

        using var responseLease = owner.TransferToLease();
        _consumer.Consume(responseLease.Value);
    }

    private sealed class NoOpResponseCompletionSource : IResponseCompletionSource
    {
        public void Complete(Response value)
        {
        }

        public void Complete()
        {
        }
    }
}
