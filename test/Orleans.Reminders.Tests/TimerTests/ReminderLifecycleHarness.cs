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
        : this(ReminderDiagnosticObserver.CreateForAllSilos(), cluster: null)
    {
    }

    public ReminderLifecycleHarness(InProcessTestCluster cluster)
        : this(ReminderDiagnosticObserver.Create(cluster), cluster)
    {
    }

    private ReminderLifecycleHarness(ReminderDiagnosticObserver diagnostics, InProcessTestCluster? cluster)
    {
        _diagnostics = diagnostics;
        _cluster = cluster;
    }

    public async Task<IReadOnlyList<SiloAddress>> WaitForServicesReadyAsync(
        IEnumerable<InProcessSiloHandle> silos,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var siloArray = silos.ToArray();
        Task<Orleans.Reminders.Diagnostics.ReminderEvents.ReminderServiceStarted>[]? startedTasks = null;
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        return await WaitForPhaseAsync(
            async phaseCancellation =>
            {
                startedTasks = siloArray
                    .Select(silo => _diagnostics.WaitForReminderServiceStartedAsync(phaseCancellation, silo.SiloAddress))
                    .ToArray();
                return (await Task.WhenAll(startedTasks)).Select(started => started.SiloAddress!).ToArray();
            },
            timeoutCancellation.Token,
            cancellationToken,
            () =>
            {
                var missing = siloArray
                    .Where((_, index) => startedTasks is null || !startedTasks[index].IsCompletedSuccessfully)
                    .Select(silo => silo.SiloAddress);
                return new TimeoutException(
                    $"Reminder services did not become ready within {timeout}. Missing silos: {string.Join(", ", missing)}.");
            });
    }

    public async Task WaitForTopologyReconciledAsync(
        Task topologyConverged,
        IEnumerable<InProcessSiloHandle> addedSilos,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var addedSiloArray = addedSilos.ToArray();
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var phaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCancellation.Token,
            cancellationToken);
        try
        {
            await topologyConverged.WaitAsync(phaseCancellation.Token);
            await ReminderTopologyStabilizer.WaitForReconciledTopologyAsync(
                GetCluster(),
                _diagnostics,
                addedSiloArray,
                phaseCancellation.Token);
        }
        catch (OperationCanceledException exception) when (
            timeoutCancellation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Reminder topology did not reconcile within {timeout}. "
                + $"Added silos: {string.Join(", ", addedSiloArray.Select(static silo => silo.SiloAddress))}. "
                + exception.Message,
                exception);
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
        => _diagnostics.WaitForGlobalQuiescenceAsync(
            GetCluster().GetActiveSilos().Select(static silo => silo.SiloAddress).ToHashSet(),
            cancellationToken);

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
        CancellationTokenSource startupCancellation,
        CancellationToken cleanupCancellationToken)
        where T : notnull
    {
        await startupCancellation.CancelAsync();
        try
        {
            await startupTask.WaitAsync(cleanupCancellationToken);
        }
        catch (Exception exception) when (!cleanupCancellationToken.IsCancellationRequested)
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

    internal static async Task WaitForPhaseAsync(
        Func<CancellationToken, Task> phase,
        CancellationToken timeoutCancellationToken,
        CancellationToken cancellationToken,
        Func<TimeoutException> createTimeoutException)
    {
        await WaitForPhaseAsync(
            async phaseCancellation =>
            {
                await phase(phaseCancellation);
                return true;
            },
            timeoutCancellationToken,
            cancellationToken,
            createTimeoutException);
    }

    private static async Task<T> WaitForPhaseAsync<T>(
        Func<CancellationToken, Task<T>> phase,
        CancellationToken timeoutCancellationToken,
        CancellationToken cancellationToken,
        Func<TimeoutException> createTimeoutException)
    {
        using var phaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCancellationToken,
            cancellationToken);
        try
        {
            return await phase(phaseCancellation.Token);
        }
        catch (OperationCanceledException) when (
            timeoutCancellationToken.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw createTimeoutException();
        }
    }

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
