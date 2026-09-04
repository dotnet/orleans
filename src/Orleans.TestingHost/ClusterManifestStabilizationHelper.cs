using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

    internal static async Task WaitForExpectedClusterManifestAsync(
        IReadOnlyCollection<SiloHandle> activeSilos,
        IReadOnlyCollection<IClusterManifestProvider> manifestProviders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activeSilos);
        ArgumentNullException.ThrowIfNull(manifestProviders);

        if (activeSilos.Count != manifestProviders.Count)
        {
            throw new ArgumentException(
                "The number of manifest providers must match the number of active silos.",
                nameof(manifestProviders));
        }

        var expectedSilos = activeSilos.Select(static silo => silo.SiloAddress).ToHashSet();
        await Task.WhenAll(manifestProviders.Select(provider =>
            WaitForExpectedClusterManifestAsync(provider, expectedSilos, cancellationToken)));
    }

    private static async Task WaitForExpectedClusterManifestAsync(
        IClusterManifestProvider manifestProvider,
        HashSet<SiloAddress> expectedSilos,
        CancellationToken cancellationToken)
    {
        if (expectedSilos.SetEquals(manifestProvider.Current.Silos.Keys))
        {
            return;
        }

        await foreach (var manifest in manifestProvider.Updates.WithCancellation(cancellationToken))
        {
            if (expectedSilos.SetEquals(manifest.Silos.Keys))
            {
                return;
            }
        }

        throw new InvalidOperationException("The cluster manifest update stream completed before the expected topology was observed.");
    }
}
