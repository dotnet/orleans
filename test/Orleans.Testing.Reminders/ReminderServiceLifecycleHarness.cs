#nullable enable

using Microsoft.Extensions.DependencyInjection;
using Orleans.Reminders.Diagnostics;
using Orleans.Reminders.TestKit;
using Orleans.Runtime;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.ReminderService;
using Orleans.TestingHost;

namespace Orleans.Testing.Reminders;

/// <summary>
/// Adapts an in-process cluster, <see cref="ReminderTestClock"/>, and
/// <see cref="ReminderDiagnosticObserver"/> to the shared service lifecycle conformance runner.
/// </summary>
public sealed class ReminderServiceLifecycleHarness : IReminderServiceLifecycleHarness
{
    private readonly InProcessTestCluster _cluster;
    private readonly ReminderTestClock _clock;
    private readonly ReminderDiagnosticObserver _observer;

    /// <summary>Initializes the harness.</summary>
    public ReminderServiceLifecycleHarness(
        InProcessTestCluster cluster,
        ReminderTestClock clock,
        ReminderDiagnosticObserver observer,
        TimeSpan reminderLoadingWindow)
    {
        _cluster = cluster ?? throw new ArgumentNullException(nameof(cluster));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        ReminderLoadingWindow = reminderLoadingWindow;
    }

    /// <inheritdoc />
    public IGrainFactory GrainFactory => _cluster.Client;

    /// <inheritdoc />
    public IReminderTable ReminderTable
        => _cluster.GetActiveSilos().First().ServiceProvider.GetRequiredService<IReminderTable>();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => _clock.UtcNow;

    /// <inheritdoc />
    public TimeSpan ReminderLoadingWindow { get; }

    /// <inheritdoc />
    public TimeSpan ReminderRefreshPeriod => _clock.RefreshReminderListPeriod;

    /// <inheritdoc />
    public IReadOnlyList<SiloAddress> ActiveSilos
        => _cluster.GetActiveSilos().Select(silo => silo.SiloAddress).Order().ToArray();

    /// <inheritdoc />
    public async Task WaitForStartupReadinessAsync(CancellationToken cancellationToken)
    {
        var startupEvents = ActiveSilos
            .Select(silo => _observer.WaitForReminderServiceStartedAsync(cancellationToken, silo))
            .ToArray();
        await Task.WhenAll(startupEvents);
        await WaitForTopologyReconciliationAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task AdvanceAsync(TimeSpan amount, CancellationToken cancellationToken)
        => _clock.AdvanceAsync(amount, cancellationToken);

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        foreach (var silo in _cluster.GetActiveSilos())
        {
            await silo.ServiceProvider.GetRequiredService<LocalReminderService>()
                .TestOnlyRefresh()
                .WaitAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public Task WaitForOwnerCountAsync(
        GrainId grainId,
        string reminderName,
        int count,
        CancellationToken cancellationToken)
        => _observer.WaitForActiveReminderCountAsync(
            grainId,
            count,
            cancellationToken,
            reminderName,
            ActiveSilos);

    /// <inheritdoc />
    public IReadOnlyList<SiloAddress> GetOwners(GrainId grainId, string reminderName)
    {
        return _observer.GetActiveReminderOwnerSilos(grainId, reminderName, ActiveSilos);
    }

    /// <inheritdoc />
    public bool IsOwner(SiloAddress siloAddress, GrainId grainId)
    {
        var silo = _cluster.GetSiloForAddress(siloAddress)
            ?? throw new InvalidOperationException($"Silo {siloAddress} is not active.");
        return silo.ServiceProvider
            .GetRequiredService<IConsistentRingProvider>()
            .GetMyRange()
            .InRange(grainId);
    }

    /// <inheritdoc />
    public Task WaitForScheduleAsync(GrainId grainId, string reminderName, CancellationToken cancellationToken)
        => _observer.WaitForLocalReminderScheduleAsync(grainId, reminderName, cancellationToken);

    /// <inheritdoc />
    public int GetLocalStartCount(GrainId grainId, string reminderName)
        => _observer.GetLocalStartCount(grainId, reminderName);

    /// <inheritdoc />
    public int GetLocalStopCount(GrainId grainId, string reminderName)
        => _observer.GetLocalStopCount(grainId, reminderName);

    /// <inheritdoc />
    public int GetScheduleChangeCount(GrainId grainId, string reminderName)
        => _observer.GetScheduleChangeCount(grainId, reminderName);

    /// <inheritdoc />
    public Task WaitForScheduleChangeCountAsync(
        GrainId grainId,
        string reminderName,
        int count,
        CancellationToken cancellationToken)
        => _observer.WaitForScheduleChangeCountAsync(grainId, reminderName, count, cancellationToken);

    /// <inheritdoc />
    public Task WaitForTickCountAsync(
        GrainId grainId,
        string reminderName,
        int count,
        CancellationToken cancellationToken)
        => _observer.WaitForTickCountAsync(grainId, count, cancellationToken, reminderName);

    /// <inheritdoc />
    public int GetTickCount(GrainId grainId, string reminderName)
        => _observer.GetTickCount(grainId, reminderName);

    /// <inheritdoc />
    public async Task<SiloAddress> JoinOneSiloAsync(CancellationToken cancellationToken)
    {
        var silo = AssertSingle(await _cluster.StartSilosAsync(1).WaitAsync(cancellationToken));
        await Task.WhenAll(
            _observer.WaitForReminderServiceStartedAsync(cancellationToken, silo.SiloAddress),
            _cluster.WaitForLivenessToStabilizeAsync().WaitAsync(cancellationToken),
            _cluster.WaitForClusterManifestToStabilizeAsync().WaitAsync(cancellationToken));
        return silo.SiloAddress;
    }

    /// <inheritdoc />
    public async Task LeaveSiloAsync(SiloAddress siloAddress, CancellationToken cancellationToken)
    {
        var silo = _cluster.GetSiloForAddress(siloAddress)
            ?? throw new InvalidOperationException($"Silo {siloAddress} is not active.");
        await _cluster.StopSiloAsync(silo, cancellationToken);
        await Task.WhenAll(
            _cluster.WaitForLivenessToStabilizeAsync().WaitAsync(cancellationToken),
            _cluster.WaitForClusterManifestToStabilizeAsync().WaitAsync(cancellationToken));
    }

    /// <inheritdoc />
    public async Task WaitForTopologyReconciliationAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            _cluster.WaitForLivenessToStabilizeAsync().WaitAsync(cancellationToken),
            _cluster.WaitForClusterManifestToStabilizeAsync().WaitAsync(cancellationToken));
        var barriers = _cluster.GetActiveSilos().Select(silo =>
            silo.ServiceProvider.GetRequiredService<LocalReminderService>()
                .TestOnlyWaitForRangeChangeReconciliation(cancellationToken));
        await Task.WhenAll(barriers);
        await RefreshAsync(cancellationToken);
    }

    private static InProcessSiloHandle AssertSingle(IReadOnlyList<InProcessSiloHandle> silos)
        => silos.Count == 1
            ? silos[0]
            : throw new InvalidOperationException($"Expected one new silo, observed {silos.Count}.");

    internal object AddDuplicateOwnerForTesting(GrainId grainId, string reminderName)
    {
        var identity = new object();
        ReminderEvents.EmitLocalReminderStarted(
            grainId,
            reminderName,
            identity,
            ActiveSilos.First());
        return identity;
    }

    internal void RemoveDuplicateOwnerForTesting(
        GrainId grainId,
        string reminderName,
        object identity)
        => ReminderEvents.EmitLocalReminderStopped(
            grainId,
            reminderName,
            identity,
            ReminderEvents.LocalReminderStopReason.Unregistered,
            ActiveSilos.First());
}
