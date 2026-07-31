using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Core.Diagnostics;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.TestHooks;

#nullable enable
namespace Orleans.TestingHost;

internal static class LivenessStabilizationHelper
{
    public static async Task<bool> WaitForExpectedActiveSilosAndGatewaysAsync(
        IReadOnlyCollection<SiloHandle> activeSilos,
        IReadOnlyCollection<ITestHooks> testHooks,
        GatewayManager gatewayManager,
        TimeSpan timeout,
        Func<TimeSpan, Task<bool>>? waitForGrainDirectoryConvergence = null)
    {
        ArgumentNullException.ThrowIfNull(gatewayManager);

        var stopwatch = Stopwatch.StartNew();
        if (!await WaitForExpectedActiveSilosAsync(activeSilos, testHooks, timeout))
        {
            return false;
        }

        var remaining = GetRemainingTime(timeout, stopwatch.Elapsed);
        if (waitForGrainDirectoryConvergence is not null
            && !await waitForGrainDirectoryConvergence(remaining))
        {
            return false;
        }

        remaining = GetRemainingTime(timeout, stopwatch.Elapsed);
        return await WaitForExpectedActiveGatewaysAsync(activeSilos, gatewayManager, remaining);
    }

    public static async Task<bool> WaitForExpectedActiveSilosAsync(
        IReadOnlyCollection<SiloHandle> activeSilos,
        IReadOnlyCollection<ITestHooks> testHooks,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(activeSilos);
        ArgumentNullException.ThrowIfNull(testHooks);

        if (activeSilos.Count == 0)
        {
            await Task.Delay(timeout);
            return false;
        }

        var expectedActiveSilos = activeSilos.Select(static silo => silo.SiloAddress).ToArray();
        try
        {
            var waitTasks = testHooks.Select(hooks => hooks.WaitForActiveSilos(expectedActiveSilos, timeout));
            var results = await Task.WhenAll(waitTasks).WaitAsync(timeout);
            return results.All(static result => result);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static TimeSpan GetRemainingTime(TimeSpan timeout, TimeSpan elapsed)
    {
        var remaining = timeout - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static async Task<bool> WaitForExpectedActiveGatewaysAsync(
        IReadOnlyCollection<SiloHandle> activeSilos,
        GatewayManager gatewayManager,
        TimeSpan timeout)
    {
        var expectedGateways = GetExpectedGatewayAddresses(activeSilos);
        if (GatewaysMatch(gatewayManager.GetLiveGateways(), expectedGateways))
        {
            return true;
        }

        if (timeout <= TimeSpan.Zero)
        {
            return false;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = GatewayEvents.AllEvents.Subscribe(new GatewayListUpdatedObserver(gatewayManager, expectedGateways, completion));

        if (GatewaysMatch(gatewayManager.GetLiveGateways(), expectedGateways))
        {
            return true;
        }

        gatewayManager.ExpediteUpdateLiveGatewaysSnapshot();

        try
        {
            await completion.Task.WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return GatewaysMatch(gatewayManager.GetLiveGateways(), expectedGateways);
        }
    }

    private static HashSet<SiloAddress> GetExpectedGatewayAddresses(IReadOnlyCollection<SiloHandle> activeSilos)
    {
        return activeSilos
            .Select(static silo => silo.GatewayAddress)
            .OfType<SiloAddress>()
            .Where(static gateway => gateway.Endpoint.Port > 0)
            .Select(static gateway => SiloAddress.New(gateway.Endpoint, 0))
            .ToHashSet();
    }

    private static bool GatewaysMatch(IEnumerable<SiloAddress> liveGateways, HashSet<SiloAddress> expectedGateways)
    {
        return expectedGateways.SetEquals(liveGateways);
    }

    private sealed class GatewayListUpdatedObserver(
        GatewayManager gatewayManager,
        HashSet<SiloAddress> expectedGateways,
        TaskCompletionSource completion) : IObserver<GatewayEvents.GatewayEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error) => completion.TrySetException(error);

        public void OnNext(GatewayEvents.GatewayEvent value)
        {
            if (value is GatewayEvents.GatewayListUpdated update
                && ReferenceEquals(update.Source, gatewayManager)
                && GatewaysMatch(update.LiveGateways, expectedGateways))
            {
                completion.TrySetResult();
            }
        }
    }
}
