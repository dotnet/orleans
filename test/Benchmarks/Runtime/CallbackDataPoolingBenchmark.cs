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
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _serviceProvider.Dispose();

    [Benchmark(Baseline = true)]
    public void AllocateAndInitialize()
    {
        var callback = new CallbackData();
        callback.Initialize(_shared, _completion, _message, _instruments);
        _consumer.Consume(callback);
    }

    [Benchmark]
    public void RentLeaseAndReturn()
    {
        var owner = CallbackDataPool.Rent(_shared, _completion, _message, _instruments, out var lease);
        _consumer.Consume(lease.Value);
        CallbackDataPool.Return(owner);
        lease.Dispose();
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
