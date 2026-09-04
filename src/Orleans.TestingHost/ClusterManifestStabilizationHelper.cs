using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.TestHooks;

namespace Orleans.TestingHost;

internal static class ClusterManifestStabilizationHelper
{
    public static async Task<bool> WaitForExpectedClusterManifestAsync(
        IReadOnlyCollection<SiloHandle> activeSilos,
        IReadOnlyCollection<ITestHooks> testHooks,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(activeSilos);
        ArgumentNullException.ThrowIfNull(testHooks);

        return await WaitForExpectedClusterManifestAsync(
            activeSilos,
            testHooks.Select(static hooks => new Func<SiloAddress[], TimeSpan, Task<bool>>(hooks.WaitForClusterManifest)).ToArray(),
            timeout);
    }

    internal static async Task<bool> WaitForExpectedClusterManifestAsync(
        IReadOnlyCollection<SiloHandle> activeSilos,
        IReadOnlyCollection<Func<SiloAddress[], TimeSpan, Task<bool>>> waitForClusterManifest,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(activeSilos);
        ArgumentNullException.ThrowIfNull(waitForClusterManifest);

        if (activeSilos.Count == 0)
        {
            await Task.Delay(timeout);
            return false;
        }

        var expectedSilos = activeSilos.Select(static silo => silo.SiloAddress).ToArray();
        try
        {
            var waitTasks = waitForClusterManifest.Select(wait => wait(expectedSilos, timeout));
            var results = await Task.WhenAll(waitTasks);
            return results.All(static result => result);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
