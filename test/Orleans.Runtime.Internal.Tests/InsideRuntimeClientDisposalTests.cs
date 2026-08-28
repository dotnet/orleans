using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class InsideRuntimeClientDisposalTests
{
    [Fact]
    public async Task Dispose_RacingWithNewRequests_StopsTimerAndCompletesAllCallbacks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new InProcessTestClusterBuilder(1).Build();
        await cluster.DeployAsync(cancellationToken);

        var services = cluster.Silos[0].ServiceProvider;
        var runtimeClient = services.GetRequiredService<InsideRuntimeClient>();
        var grainFactory = services.GetRequiredService<IGrainFactory>();
        var typeResolver = services.GetRequiredService<GrainInterfaceTypeResolver>();
        var interfaceType = typeResolver.GetGrainInterfaceType(typeof(ILongRunningTaskGrain<int>));
        var grainId = Guid.NewGuid();
        var grain = grainFactory.GetGrain<ILongRunningTaskGrain<int>>(grainId);
        var pendingCall = grain.LongRunningTask(1, TimeSpan.FromSeconds(1));

        await WaitUntilAsync(
            () => runtimeClient.GetRunningRequestsCount(interfaceType) == 1,
            TimeSpan.FromSeconds(10),
            cancellationToken);

        using var start = new ManualResetEventSlim();
        var disposeTask = Task.Run(() =>
        {
            start.Wait();
            runtimeClient.Dispose();
        }, cancellationToken);
        var racingCall = Task.Run(async () =>
        {
            start.Wait();
            await grain.LongRunningTask(2, TimeSpan.Zero);
        }, cancellationToken);

        start.Set();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        await Assert.ThrowsAsync<SiloUnavailableException>(() => pendingCall);
        try
        {
            await racingCall.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (SiloUnavailableException)
        {
            // The call lost the admission race and was rejected by the stopping runtime.
        }
        await runtimeClient.CallbackTimerTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        Assert.Equal(0, runtimeClient.GetRunningRequestsCount(interfaceType));
        await Assert.ThrowsAsync<SiloUnavailableException>(() => grain.LongRunningTask(3, TimeSpan.Zero));

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        var externalGrain = cluster.Client.GetGrain<ILongRunningTaskGrain<int>>(grainId);
        Assert.NotEqual(3, await externalGrain.GetLastValue());
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected runtime-client state was not reached.");
            }

            await Task.Delay(10, cancellationToken);
        }
    }
}
