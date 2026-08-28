#nullable enable

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;

namespace UnitTests.TimerTests;

/// <summary>
/// Coordinates the observable phases of reminder lifecycle tests.
/// </summary>
public sealed class ReminderLifecycleHarness : IDisposable
{
    private readonly ReminderDiagnosticObserver _diagnostics;
    private readonly InProcessTestCluster? _cluster;

    public ReminderLifecycleHarness()
        : this(ReminderDiagnosticObserver.Create(), cluster: null)
    {
    }

    public ReminderLifecycleHarness(InProcessTestCluster cluster)
        : this(ReminderDiagnosticObserver.Create(), cluster)
    {
    }

    private ReminderLifecycleHarness(ReminderDiagnosticObserver diagnostics, InProcessTestCluster? cluster)
    {
        _diagnostics = diagnostics;
        _cluster = cluster;
    }

    public async Task<IReadOnlyList<SiloAddress>> WaitForServicesReadyAsync(
        IEnumerable<InProcessSiloHandle> silos,
        CancellationToken cancellationToken)
    {
        var siloArray = silos.ToArray();
        var startedTasks = siloArray
            .Select(silo => _diagnostics.WaitForReminderServiceStartedAsync(cancellationToken, silo.SiloAddress))
            .ToArray();
        try
        {
            return (await Task.WhenAll(startedTasks)).Select(started => started.SiloAddress!).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var missing = siloArray
                .Where((_, index) => !startedTasks[index].IsCompletedSuccessfully)
                .Select(silo => silo.SiloAddress);
            throw new TimeoutException(
                $"Reminder services did not become ready. Missing silos: {string.Join(", ", missing)}.");
        }
    }

    public async Task WaitForTopologyReconciledAsync(
        Task topologyConverged,
        IEnumerable<InProcessSiloHandle> addedSilos,
        CancellationToken cancellationToken)
    {
        var serviceReady = WaitForServicesReadyAsync(addedSilos, cancellationToken);
        await Task.WhenAll(topologyConverged.WaitAsync(cancellationToken), serviceReady);

        foreach (var silo in GetCluster().GetActiveSilos())
        {
            await silo.ServiceProvider
                .GetRequiredService<LocalReminderService>()
                .TestOnlyWaitForRangeChangeReconciliation(cancellationToken);
        }
    }

    public async Task RefreshActiveServicesAsync(CancellationToken cancellationToken)
    {
        foreach (var silo in GetCluster().GetActiveSilos())
        {
            await silo.ServiceProvider
                .GetRequiredService<LocalReminderService>()
                .TestOnlyRefresh()
                .WaitAsync(cancellationToken);
        }
    }

    public async Task WaitForSchedulesArmedAsync(
        CancellationToken cancellationToken,
        params (IAddressable Grain, string ReminderName)[] reminders)
    {
        await RefreshActiveServicesAsync(cancellationToken);
        await Task.WhenAll(reminders.Select(async reminder =>
        {
            await _diagnostics.WaitForActiveReminderCountAsync(
                reminder.Grain,
                1,
                cancellationToken,
                reminder.ReminderName);
            await _diagnostics.WaitForLocalReminderScheduleAsync(
                reminder.Grain,
                reminder.ReminderName,
                cancellationToken);
        }));
    }

    public TickCompletionPhase ArmTickCompletion(
        CancellationToken cancellationToken,
        params (IAddressable Grain, string ReminderName)[] reminders)
    {
        var entries = new TickCompletionEntry[reminders.Length];
        for (var i = 0; i < reminders.Length; i++)
        {
            var reminder = reminders[i];
            var previousCount = GetTickCount(reminder.Grain.GetGrainId(), reminder.ReminderName);
            entries[i] = new(
                reminder.Grain.GetGrainId(),
                reminder.ReminderName,
                previousCount,
                _diagnostics.WaitForTickCountAsync(
                    reminder.Grain,
                    previousCount + 1,
                    cancellationToken,
                    reminder.ReminderName));
        }

        return new TickCompletionPhase(entries);
    }

    public Task WaitForTickCompletedAsync(TickCompletionPhase phase)
        => Task.WhenAll(phase.Entries.Select(entry => entry.Completion));

    public Task WaitForGlobalQuiescenceAsync(CancellationToken cancellationToken)
        => _diagnostics.WaitForGlobalQuiescenceAsync(cancellationToken);

    public Task WaitForReminderQuiescenceAsync(
        IAddressable grain,
        string reminderName,
        CancellationToken cancellationToken)
        => _diagnostics.WaitForReminderQuiescenceAsync(grain, reminderName, cancellationToken);

    public Task WaitForReminderQuiescenceAsync(
        GrainId grainId,
        string reminderName,
        CancellationToken cancellationToken)
        => _diagnostics.WaitForReminderQuiescenceAsync(grainId, reminderName, cancellationToken);

    public Task WaitForReminderRegisteredAsync(
        IAddressable grain,
        string reminderName,
        CancellationToken cancellationToken)
        => _diagnostics.WaitForReminderRegisteredAsync(grain, reminderName, cancellationToken);

    public Task WaitForReminderRegisteredAsync(
        GrainId grainId,
        string reminderName,
        CancellationToken cancellationToken)
        => _diagnostics.WaitForReminderRegisteredAsync(grainId, reminderName, cancellationToken);

    public Task WaitForReminderUnregisteredAsync(
        IAddressable grain,
        string reminderName,
        CancellationToken cancellationToken)
        => _diagnostics.WaitForReminderUnregisteredAsync(grain, reminderName, cancellationToken);

    public Task WaitForReminderUnregisteredAsync(
        GrainId grainId,
        string reminderName,
        CancellationToken cancellationToken)
        => _diagnostics.WaitForReminderUnregisteredAsync(grainId, reminderName, cancellationToken);

    public Task WaitForActiveReminderCountAsync(
        IAddressable grain,
        int expectedCount,
        CancellationToken cancellationToken,
        string reminderName)
        => _diagnostics.WaitForActiveReminderCountAsync(grain, expectedCount, cancellationToken, reminderName);

    public Task WaitForActiveReminderCountAsync(
        GrainId grainId,
        int expectedCount,
        CancellationToken cancellationToken,
        string reminderName)
        => _diagnostics.WaitForActiveReminderCountAsync(grainId, expectedCount, cancellationToken, reminderName);

    public Task WaitForLocalReminderScheduleAsync(
        IAddressable grain,
        string reminderName,
        CancellationToken cancellationToken)
        => _diagnostics.WaitForLocalReminderScheduleAsync(grain, reminderName, cancellationToken);

    public Task WaitForLocalReminderScheduleAsync(
        GrainId grainId,
        string reminderName,
        CancellationToken cancellationToken)
        => _diagnostics.WaitForLocalReminderScheduleAsync(grainId, reminderName, cancellationToken);

    public Task<Orleans.Reminders.Diagnostics.ReminderEvents.TickCompleted> WaitForReminderTickAsync(
        IAddressable grain,
        CancellationToken cancellationToken,
        string reminderName)
        => _diagnostics.WaitForReminderTickAsync(grain, cancellationToken, reminderName);

    public Task<Orleans.Reminders.Diagnostics.ReminderEvents.TickCompleted> WaitForReminderTickAsync(
        GrainId grainId,
        CancellationToken cancellationToken,
        string reminderName)
        => _diagnostics.WaitForReminderTickAsync(grainId, cancellationToken, reminderName);

    public Task WaitForTickCountAsync(
        IAddressable grain,
        int expectedCount,
        CancellationToken cancellationToken,
        string reminderName)
        => _diagnostics.WaitForTickCountAsync(grain, expectedCount, cancellationToken, reminderName);

    public Task WaitForTickCountAsync(
        GrainId grainId,
        int expectedCount,
        CancellationToken cancellationToken,
        string reminderName)
        => _diagnostics.WaitForTickCountAsync(grainId, expectedCount, cancellationToken, reminderName);

    public int GetTickCount(GrainId grainId, string reminderName)
        => _diagnostics.GetTickCount(grainId, reminderName);

    public int GetActiveReminderCount(GrainId grainId, string reminderName)
        => _diagnostics.GetActiveReminderCount(grainId, reminderName);

    public SiloAddress[] GetActiveReminderSilos(GrainId grainId, string reminderName)
        => _diagnostics.GetActiveReminderSilos(grainId, reminderName);

    internal static async Task CleanupPartialStartupAsync<T>(
        IReadOnlySet<T> initialResources,
        Task startupTask,
        Func<IReadOnlyList<T>> getActiveResources,
        Func<T, Task> stopResource,
        Func<Task> waitForTopology,
        ILogger logger,
        CancellationToken startupWaitCancellationToken,
        CancellationToken cleanupCancellationToken)
        where T : notnull
    {
        try
        {
            await startupTask.WaitAsync(startupWaitCancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogInformation(exception, "Additional resource startup did not complete successfully before cleanup.");
        }

        var additionalResources = getActiveResources()
            .Where(resource => !initialResources.Contains(resource))
            .ToArray();
        if (additionalResources.Length == 0)
        {
            return;
        }

        await Task.WhenAll(additionalResources.Select(stopResource)).WaitAsync(cleanupCancellationToken);
        await waitForTopology().WaitAsync(cleanupCancellationToken);
    }

    public void Dispose() => _diagnostics.Dispose();

    private InProcessTestCluster GetCluster()
        => _cluster ?? throw new InvalidOperationException("This reminder harness is not attached to a test cluster.");

    public sealed class TickCompletionPhase
    {
        internal TickCompletionPhase(IReadOnlyList<TickCompletionEntry> entries)
        {
            Entries = entries;
        }

        internal IReadOnlyList<TickCompletionEntry> Entries { get; }

        public IReadOnlyList<(GrainId GrainId, string ReminderName, int PreviousCount)> Snapshot
            => Entries.Select(entry => (entry.GrainId, entry.ReminderName, entry.PreviousCount)).ToArray();
    }

    internal sealed record TickCompletionEntry(
        GrainId GrainId,
        string ReminderName,
        int PreviousCount,
        Task Completion);
}
