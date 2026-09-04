using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Orleans.TestingHost;

namespace Orleans.Testing.Reminders;

/// <summary>
/// Establishes the topology boundary required by deterministic reminder tests.
/// </summary>
public static class ReminderTopologyStabilizer
{
    /// <summary>
    /// Waits for reminder service readiness, stable membership visibility, and current reminder reconciliation.
    /// </summary>
    public static async Task<IReadOnlyList<InProcessSiloHandle>> WaitForStableTopologyAsync(
        InProcessTestCluster cluster,
        ReminderDiagnosticObserver observer,
        IEnumerable<InProcessSiloHandle> readySilos,
        CancellationToken cancellationToken)
        => await WaitForStableTopologyAsync(
            cluster,
            observer,
            readySilos,
            useInitialLoadForSingleSilo: false,
            cancellationToken);

    /// <summary>
    /// Waits for the initial reminder topology, using the completed initial load as the refresh boundary for a
    /// single-silo cluster.
    /// </summary>
    public static async Task<IReadOnlyList<InProcessSiloHandle>> WaitForStartupTopologyAsync(
        InProcessTestCluster cluster,
        ReminderDiagnosticObserver observer,
        IEnumerable<InProcessSiloHandle> readySilos,
        CancellationToken cancellationToken)
        => await WaitForStableTopologyAsync(
            cluster,
            observer,
            readySilos,
            useInitialLoadForSingleSilo: true,
            cancellationToken);

    private static async Task<IReadOnlyList<InProcessSiloHandle>> WaitForStableTopologyAsync(
        InProcessTestCluster cluster,
        ReminderDiagnosticObserver observer,
        IEnumerable<InProcessSiloHandle> readySilos,
        bool useInitialLoadForSingleSilo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(readySilos);

        var requiredReadySilos = readySilos
            .DistinctBy(silo => silo.SiloAddress)
            .OrderBy(silo => silo.SiloAddress)
            .ToArray();
        var expectedActiveSilos = Array.Empty<InProcessSiloHandle>();
        var phase = "reminder service readiness";
        try
        {
            var ready = requiredReadySilos
                .Select(silo => observer.WaitForReminderServiceStartedAsync(
                    cancellationToken,
                    silo.SiloAddress))
                .ToArray();
            await Task.WhenAll(ready);

            while (true)
            {
                phase = "liveness and manifest convergence";
                await Task.WhenAll(
                    cluster.WaitForLivenessToStabilizeAsync().WaitAsync(cancellationToken),
                    cluster.WaitForClusterManifestToStabilizeAsync().WaitAsync(cancellationToken));

                phase = "active silo selection";
                expectedActiveSilos = cluster.GetActiveSilos()
                    .OrderBy(silo => silo.SiloAddress)
                    .ToArray();
                var expectedAddresses = expectedActiveSilos.Select(silo => silo.SiloAddress).ToArray();
                var missingReadySilos = requiredReadySilos
                    .Select(silo => silo.SiloAddress)
                    .Except(expectedAddresses)
                    .ToArray();
                if (missingReadySilos.Length > 0)
                {
                    throw new InvalidOperationException(
                        $"Ready reminder services are not active: [{string.Join(", ", missingReadySilos.Select(silo => silo.ToString()))}]. "
                        + $"Active silos: [{string.Join(", ", expectedAddresses.Select(silo => silo.ToString()))}].");
                }

                phase = "selected active reminder service readiness";
                var activeServicesReady = expectedActiveSilos
                    .Select(silo => observer.WaitForReminderServiceStartedAsync(
                        cancellationToken,
                        silo.SiloAddress))
                    .ToArray();
                await Task.WhenAll(activeServicesReady);

                var reminderServices = expectedActiveSilos
                    .Select(silo => silo.ServiceProvider.GetRequiredService<LocalReminderService>())
                    .ToArray();
                var selectedMembershipVersions = reminderServices
                    .Select(service => service.TestOnlyGetMembershipVersions().Current)
                    .ToArray();

                phase = "silo status listener delivery";
                await Task.WhenAll(reminderServices.Select(service =>
                    service.TestOnlyWaitForSiloStatusListeners(cancellationToken)));

                var initialLoadIsRefreshBoundary = useInitialLoadForSingleSilo
                    && requiredReadySilos.Length == 1
                    && expectedActiveSilos.Length == 1
                    && requiredReadySilos[0].SiloAddress.Equals(expectedActiveSilos[0].SiloAddress);
                if (!initialLoadIsRefreshBoundary)
                {
                    phase = "stable topology refresh start";
                    var refreshesStarted = reminderServices.Select(service => service.TestOnlyStartRefresh()).ToArray();
                    await Task.WhenAll(refreshesStarted).WaitAsync(cancellationToken);
                }

                phase = "latest reminder reconciliation";
                var reconciliations = reminderServices.Select(service =>
                    service.TestOnlyWaitForRangeChangeReconciliation(cancellationToken)).ToArray();
                await Task.WhenAll(reconciliations);

                phase = "active silo and membership version verification";
                var observedAddresses = cluster.GetActiveSilos()
                    .Select(silo => silo.SiloAddress)
                    .Order()
                    .ToArray();
                var observedMembershipVersions = reminderServices
                    .Select(service => service.TestOnlyGetMembershipVersions())
                    .ToArray();
                var expectedAddressSet = expectedAddresses.ToHashSet();
                var manifestsConverged = expectedActiveSilos.All(silo =>
                    expectedAddressSet.SetEquals(
                        silo.ServiceProvider.GetRequiredService<IClusterManifestProvider>().Current.Silos.Keys));
                var topologyChanged = !observedAddresses.SequenceEqual(expectedAddresses)
                    || !manifestsConverged
                    || observedMembershipVersions.Select(versions => versions.Current).Distinct().Count() != 1
                    || observedMembershipVersions
                        .Where((versions, index) =>
                            versions.Current != selectedMembershipVersions[index]
                            || versions.Processed != versions.Current)
                        .Any();
                if (!topologyChanged)
                {
                    return expectedActiveSilos;
                }
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                DescribeFailure(cluster, expectedActiveSilos, phase),
                exception,
                cancellationToken);
        }
    }

    private static string DescribeFailure(
        InProcessTestCluster cluster,
        IReadOnlyList<InProcessSiloHandle> expectedActiveSilos,
        string phase)
    {
        var expected = expectedActiveSilos.Select(silo => silo.SiloAddress.ToString());
        var observed = cluster.GetActiveSilos()
            .OrderBy(silo => silo.SiloAddress)
            .ToArray();
        var states = observed.Select(silo =>
        {
            var reminderState = silo.ServiceProvider
                .GetRequiredService<LocalReminderService>()
                .TestOnlyDescribeTopologyState();
            var manifestSilos = silo.ServiceProvider
                .GetRequiredService<IClusterManifestProvider>()
                .Current
                .Silos
                .Keys
                .Order();
            return $"{reminderState}, manifestSilos=[{string.Join(", ", manifestSilos)}]";
        });
        return $"Reminder topology stabilization was canceled during '{phase}'. "
            + $"Expected active silos: [{string.Join(", ", expected)}]. "
            + $"Observed active silos: [{string.Join(", ", observed.Select(silo => silo.SiloAddress.ToString()))}]. "
            + $"States: [{string.Join("; ", states)}].";
    }
}
