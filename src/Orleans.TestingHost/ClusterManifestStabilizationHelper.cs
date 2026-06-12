using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.TestHooks;

#nullable enable
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

        if (activeSilos.Count == 0)
        {
            await Task.Delay(timeout);
            return false;
        }

        var expectedSilos = activeSilos.Select(static silo => silo.SiloAddress).ToArray();
        try
        {
            var waitTasks = testHooks.Select(hooks => hooks.WaitForClusterManifest(expectedSilos, timeout));
            var results = await Task.WhenAll(waitTasks).WaitAsync(timeout);
            return results.All(static result => result);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
