using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;

#nullable enable
namespace Orleans.TestingHost;

internal static class LivenessStabilizationHelper
{
    public static async Task<bool> WaitForExpectedActiveSilosAsync(
        IClusterClient client,
        IReadOnlyCollection<SiloHandle> activeSilos,
        TimeSpan fallbackTimeout)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(activeSilos);

        if (activeSilos.Count == 0)
        {
            await Delay(fallbackTimeout);
            return false;
        }

        var expectedActiveSilos = activeSilos.Select(static silo => silo.SiloAddress).ToArray();
        try
        {
            var waitTasks = activeSilos.Select(silo => client.GetTestHooks(silo).WaitForActiveSilos(expectedActiveSilos, fallbackTimeout));
            var results = await Task.WhenAll(waitTasks).WaitAsync(fallbackTimeout);
            return results.All(static result => result);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static Task Delay(TimeSpan delay) => delay > TimeSpan.Zero ? Task.Delay(delay) : Task.CompletedTask;
}
