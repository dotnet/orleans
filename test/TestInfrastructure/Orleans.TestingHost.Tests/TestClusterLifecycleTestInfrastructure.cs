using System.Net;
using Microsoft.Extensions.Configuration;
using Orleans.Runtime;

namespace Orleans.TestingHost.Tests;

internal static class TestClusterLifecycleTestInfrastructure
{
    public static TestCluster CreateCluster(short initialSilosCount, RecordingPortAllocator allocator) =>
        new(
            new TestClusterOptions
            {
                ClusterId = "test-cluster-lifecycle",
                ServiceId = "test-service-lifecycle",
                BaseSiloPort = 24_000,
                BaseGatewayPort = 34_000,
                InitialSilosCount = initialSilosCount,
                InitializeClientOnDeploy = false,
                UseTestClusterMembership = true,
            },
            Array.Empty<IConfigurationSource>(),
            allocator);

    public static RecordingSiloHandle CreateHandle(
        string name,
        IConfiguration configuration,
        int generation,
        bool blockStop = false)
    {
        var siloPort = int.Parse(configuration["Orleans:Endpoints:SiloPort"]!);
        var gatewayPort = int.Parse(configuration["Orleans:Endpoints:GatewayPort"]!);
        return new RecordingSiloHandle(blockStop)
        {
            Name = name,
            SiloAddress = SiloAddress.New(IPAddress.Loopback, siloPort, generation),
            GatewayAddress = SiloAddress.New(IPAddress.Loopback, gatewayPort, generation),
        };
    }

    public static TaskCompletionSource CreateCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class RecordingSiloHandle(bool blockStop = false) : SiloHandle
{
    private readonly TaskCompletionSource _allowStop = CreateStopRelease(blockStop);
    private int _active = 1;
    private int _disposeCount;
    private int _gracefulStopCount;
    private int _killCount;

    public override bool IsActive => Volatile.Read(ref _active) == 1;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public int GracefulStopCount => Volatile.Read(ref _gracefulStopCount);

    public int KillCount => Volatile.Read(ref _killCount);

    public TaskCompletionSource StopEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void AllowStop() => _allowStop.TrySetResult();

    public void SetInactive() => Volatile.Write(ref _active, 0);

    public override Task StopSiloAsync(bool stopGracefully) =>
        StopSiloAsync(stopGracefully, CancellationToken.None);

    public override Task StopSiloAsync(CancellationToken cancellationToken) =>
        StopSiloAsync(stopGracefully: true, cancellationToken);

    public override async Task StopSiloAsync(bool stopGracefully, CancellationToken cancellationToken)
    {
        if (stopGracefully)
        {
            Interlocked.Increment(ref _gracefulStopCount);
        }
        else
        {
            Interlocked.Increment(ref _killCount);
        }

        StopEntered.TrySetResult();
        await _allowStop.Task.WaitAsync(cancellationToken);
        Volatile.Write(ref _active, 0);
    }

    public override ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        Volatile.Write(ref _active, 0);
        return ValueTask.CompletedTask;
    }

    private static TaskCompletionSource CreateStopRelease(bool blockStop)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!blockStop)
        {
            result.SetResult();
        }

        return result;
    }
}

internal sealed class RecordingPortAllocator : ITestClusterPortAllocator
{
    private int _disposeCount;
    private int _nextPortPair;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public (int, int) AllocateConsecutivePortPairs(int numPorts)
    {
        var pair = Interlocked.Increment(ref _nextPortPair);
        return (25_000 + pair, 35_000 + pair);
    }

    public void Dispose() => Interlocked.Increment(ref _disposeCount);
}
