using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Reminders.TestKit;

/// <summary>
/// Supplies deterministic clock, diagnostics, and topology controls to
/// <see cref="ReminderServiceLifecycleTestRunner"/>.
/// </summary>
public interface IReminderServiceLifecycleHarness
{
    /// <summary>Gets the deployed cluster's grain factory.</summary>
    IGrainFactory GrainFactory { get; }

    /// <summary>Gets the provider table used by the reminder service.</summary>
    IReminderTable ReminderTable { get; }

    /// <summary>Gets the deterministic reminder clock time.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Gets the configured reminder loading window.</summary>
    TimeSpan ReminderLoadingWindow { get; }

    /// <summary>Gets the configured reminder-table refresh period.</summary>
    TimeSpan ReminderRefreshPeriod { get; }

    /// <summary>Gets the currently active silos.</summary>
    IReadOnlyList<SiloAddress> ActiveSilos { get; }

    /// <summary>Waits until every active reminder service is ready.</summary>
    Task WaitForStartupReadinessAsync(CancellationToken cancellationToken);

    /// <summary>Advances the one reminder clock driver.</summary>
    Task AdvanceAsync(TimeSpan amount, CancellationToken cancellationToken);

    /// <summary>Waits for exactly <paramref name="count"/> local owners.</summary>
    Task WaitForOwnerCountAsync(
        GrainId grainId,
        string reminderName,
        int count,
        CancellationToken cancellationToken);

    /// <summary>Gets the current local owners.</summary>
    IReadOnlyList<SiloAddress> GetOwners(GrainId grainId, string reminderName);

    /// <summary>Waits until the current owner has armed its persisted schedule.</summary>
    Task WaitForScheduleAsync(GrainId grainId, string reminderName, CancellationToken cancellationToken);

    /// <summary>Gets the number of local reminder instances started for an identity.</summary>
    int GetLocalStartCount(GrainId grainId, string reminderName);

    /// <summary>Gets the number of local reminder instances stopped for an identity.</summary>
    int GetLocalStopCount(GrainId grainId, string reminderName);

    /// <summary>Gets the number of local schedule changes for an identity.</summary>
    int GetScheduleChangeCount(GrainId grainId, string reminderName);

    /// <summary>Waits for the local schedule-change count.</summary>
    Task WaitForScheduleChangeCountAsync(
        GrainId grainId,
        string reminderName,
        int count,
        CancellationToken cancellationToken);

    /// <summary>Waits for exactly the requested completed tick count.</summary>
    Task WaitForTickCountAsync(
        GrainId grainId,
        string reminderName,
        int count,
        CancellationToken cancellationToken);

    /// <summary>Gets the current completed tick count.</summary>
    int GetTickCount(GrainId grainId, string reminderName);

    /// <summary>Starts one silo and waits for its reminder service to become ready.</summary>
    Task<SiloAddress> JoinOneSiloAsync(CancellationToken cancellationToken);

    /// <summary>Stops the specified silo.</summary>
    Task LeaveSiloAsync(SiloAddress siloAddress, CancellationToken cancellationToken);

    /// <summary>Waits for membership and reminder-range reconciliation on all active silos.</summary>
    Task WaitForTopologyReconciliationAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Shared deterministic conformance scenarios for reminder service lifecycle, ownership, and churn.
/// </summary>
/// <remarks>
/// Provider suites supply only an <see cref="IReminderServiceLifecycleHarness"/>. The runner owns identities,
/// schedules, topology transitions, exact assertions, and cleanup. Each scenario cleans only the reminders which
/// it created and verifies their absence, so unrelated rows and concurrently running provider suites are isolated.
/// </remarks>
public abstract class ReminderServiceLifecycleTestRunner
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(2);
    private readonly IReminderServiceLifecycleHarness _harness;
    private readonly int _seed;
    private int _grainCounter;

    /// <summary>Initializes the runner.</summary>
    protected ReminderServiceLifecycleTestRunner(
        IReminderServiceLifecycleHarness harness,
        string providerName,
        int seed = 0)
    {
        _harness = harness ?? throw new ArgumentNullException(nameof(harness));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ProviderName = providerName;
        _seed = seed;
    }

    /// <summary>Gets the provider name used in diagnostics.</summary>
    protected string ProviderName { get; }

    /// <summary>Guarantee: every active silo reports reminder-service readiness before operations begin.</summary>
    public virtual Task ReminderService_StartupReadiness()
        => RunReminderService_StartupReadiness(CancellationToken.None);

    /// <summary>Runs the startup-readiness scenario.</summary>
    public async Task RunReminderService_StartupReadiness(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderService_StartupReadiness);
        await ExecuteWithCleanupAsync(
            Guarantee,
            async () =>
            {
                await _harness.WaitForStartupReadinessAsync(cancellationToken);
                if (_harness.ActiveSilos.Count == 0)
                {
                    Fail(Guarantee, "startup")
                        .WithExpected("at least one ready initial silo")
                        .WithObserved($"activeSilos=[{string.Join(", ", _harness.ActiveSilos)}]")
                        .Throw();
                }
            },
            _ => Task.CompletedTask);
    }

    /// <summary>Guarantee: a registration has one owner and one exact delivery.</summary>
    public virtual Task ReminderService_RegistrationHasSingleOwner()
        => RunReminderService_RegistrationHasSingleOwner(CancellationToken.None);

    /// <summary>Runs the registration-ownership scenario.</summary>
    public async Task RunReminderService_RegistrationHasSingleOwner(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderService_RegistrationHasSingleOwner);
        const string Name = "registration-owner";
        var grain = CreateGrain(Guarantee);
        var due = TimeSpan.FromSeconds(3);
        await ExecuteWithCleanupAsync(
            Guarantee,
            async () =>
            {
                var expectedStart = _harness.UtcNow.UtcDateTime + due;
                await grain.RegisterOrUpdateAsync(Name, due, Period).WaitAsync(cancellationToken);
                await _harness.WaitForOwnerCountAsync(grain.GetGrainId(), Name, 1, cancellationToken);
                if (_harness.GetOwners(grain.GetGrainId(), Name).Count != 1)
                {
                    OwnershipFailure(Guarantee, grain.GetGrainId(), Name, 1).Throw();
                }

                await _harness.WaitForScheduleAsync(grain.GetGrainId(), Name, cancellationToken);
                await AssertPersistedAsync(Guarantee, grain.GetGrainId(), Name, expectedStart, Period, cancellationToken);
                var tick = _harness.WaitForTickCountAsync(grain.GetGrainId(), Name, 1, cancellationToken);
                await _harness.AdvanceAsync(due, cancellationToken);
                await tick;
                AssertCounts(Guarantee, grain, Name, owners: null, ticks: 1);
            },
            cleanupToken => CleanupAsync(Guarantee, grain, Name, cleanupToken));
    }

    /// <summary>Guarantee: updating a reminder changes its schedule without restarting its local owner.</summary>
    public virtual Task ReminderService_UpdateDoesNotRestartLocalOwner()
        => RunReminderService_UpdateDoesNotRestartLocalOwner(CancellationToken.None);

    /// <summary>Runs the in-place update scenario.</summary>
    public async Task RunReminderService_UpdateDoesNotRestartLocalOwner(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderService_UpdateDoesNotRestartLocalOwner);
        const string Name = "in-place-update";
        var grain = CreateGrain(Guarantee);
        await ExecuteWithCleanupAsync(
            Guarantee,
            async () =>
            {
                await grain.RegisterOrUpdateAsync(Name, TimeSpan.FromSeconds(3), Period).WaitAsync(cancellationToken);
                await _harness.WaitForOwnerCountAsync(grain.GetGrainId(), Name, 1, cancellationToken);
                await _harness.WaitForScheduleAsync(grain.GetGrainId(), Name, cancellationToken);
                var original = await ReadRequiredAsync(Guarantee, grain.GetGrainId(), Name, cancellationToken);
                var owners = _harness.GetOwners(grain.GetGrainId(), Name).ToArray();
                var starts = _harness.GetLocalStartCount(grain.GetGrainId(), Name);
                var stops = _harness.GetLocalStopCount(grain.GetGrainId(), Name);
                var scheduleChanges = _harness.GetScheduleChangeCount(grain.GetGrainId(), Name);
                var due = TimeSpan.FromSeconds(4);
                var expectedStart = _harness.UtcNow.UtcDateTime + due;

                var changed = _harness.WaitForScheduleChangeCountAsync(
                    grain.GetGrainId(),
                    Name,
                    scheduleChanges + 1,
                    cancellationToken);
                await grain.RegisterOrUpdateAsync(Name, due, Period + TimeSpan.FromMinutes(1)).WaitAsync(cancellationToken);
                await changed;
                await _harness.WaitForScheduleAsync(grain.GetGrainId(), Name, cancellationToken);
                var updated = await ReadRequiredAsync(Guarantee, grain.GetGrainId(), Name, cancellationToken);

                if (!owners.SequenceEqual(_harness.GetOwners(grain.GetGrainId(), Name))
                    || starts != _harness.GetLocalStartCount(grain.GetGrainId(), Name)
                    || stops != _harness.GetLocalStopCount(grain.GetGrainId(), Name)
                    || string.Equals(original.ETag, updated.ETag, StringComparison.Ordinal)
                    || updated.StartAt != expectedStart
                    || updated.Period != Period + TimeSpan.FromMinutes(1))
                {
                    Fail(Guarantee, "RegisterOrUpdateReminder")
                        .WithIdentity(grain.GetGrainId(), Name)
                        .WithExpected($"same single owner, starts={starts}, stops={stops}, rotated ETag, StartAt={expectedStart:O}")
                        .WithObserved(
                            $"owners=[{string.Join(", ", _harness.GetOwners(grain.GetGrainId(), Name))}], "
                            + $"starts={_harness.GetLocalStartCount(grain.GetGrainId(), Name)}, "
                            + $"stops={_harness.GetLocalStopCount(grain.GetGrainId(), Name)}, row={Describe(updated)}")
                        .WithETags(updated.ETag, original.ETag)
                        .Throw();
                }
            },
            cleanupToken => CleanupAsync(Guarantee, grain, Name, cleanupToken));
    }

    /// <summary>Guarantee: removal reaches quiescence and cannot deliver a later occurrence.</summary>
    public virtual Task ReminderService_RemovalReachesQuiescence()
        => RunReminderService_RemovalReachesQuiescence(CancellationToken.None);

    /// <summary>Runs the removal/quiescence scenario.</summary>
    public async Task RunReminderService_RemovalReachesQuiescence(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderService_RemovalReachesQuiescence);
        const string Name = "removal-quiescence";
        var grain = CreateGrain(Guarantee);
        await ExecuteWithCleanupAsync(
            Guarantee,
            async () =>
            {
                await grain.RegisterOrUpdateAsync(Name, TimeSpan.FromSeconds(3), Period).WaitAsync(cancellationToken);
                await _harness.WaitForOwnerCountAsync(grain.GetGrainId(), Name, 1, cancellationToken);
                await _harness.WaitForScheduleAsync(grain.GetGrainId(), Name, cancellationToken);

                if (!await grain.UnregisterAsync(Name).WaitAsync(cancellationToken))
                {
                    Fail(Guarantee, "UnregisterReminder")
                        .WithIdentity(grain.GetGrainId(), Name)
                        .WithExpected("successful removal")
                        .WithObserved("removal returned false")
                        .Throw();
                }

                await _harness.WaitForOwnerCountAsync(grain.GetGrainId(), Name, 0, cancellationToken);
                await AssertAbsentAsync(Guarantee, grain.GetGrainId(), Name, cancellationToken);
                await _harness.AdvanceAsync(Period, cancellationToken);
                AssertCounts(Guarantee, grain, Name, owners: 0, ticks: 0);
            },
            cleanupToken => CleanupAsync(Guarantee, grain, Name, cleanupToken));
    }

    /// <summary>Guarantee: a persisted reminder entering the loading window fires at its exact due time.</summary>
    public virtual Task ReminderService_ExactDueRecovery()
        => RunReminderService_ExactDueRecovery(CancellationToken.None);

    /// <summary>Runs the exact-due recovery scenario.</summary>
    public async Task RunReminderService_ExactDueRecovery(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderService_ExactDueRecovery);
        const string Name = "exact-due-recovery";
        var grain = CreateGrain(Guarantee);
        var due = _harness.ReminderLoadingWindow
            + _harness.ReminderRefreshPeriod
            + _harness.ReminderRefreshPeriod;
        await ExecuteWithCleanupAsync(
            Guarantee,
            async () =>
            {
                var expectedStart = _harness.UtcNow.UtcDateTime + due;
                await grain.RegisterOrUpdateAsync(Name, due, Period).WaitAsync(cancellationToken);
                var preWindowAdvance = due - _harness.ReminderLoadingWindow - _harness.ReminderRefreshPeriod;
                await _harness.AdvanceAsync(preWindowAdvance, cancellationToken);
                if (_harness.GetOwners(grain.GetGrainId(), Name).Count != 0)
                {
                    Fail(Guarantee, "before loading window")
                        .WithIdentity(grain.GetGrainId(), Name)
                        .WithExpected("no local owner immediately before entering the loading window")
                        .WithObserved($"owners=[{string.Join(", ", _harness.GetOwners(grain.GetGrainId(), Name))}]")
                        .Throw();
                }

                var owner = _harness.WaitForOwnerCountAsync(grain.GetGrainId(), Name, 1, cancellationToken);
                await _harness.AdvanceAsync(_harness.ReminderRefreshPeriod, cancellationToken);
                await owner;
                await _harness.WaitForScheduleAsync(grain.GetGrainId(), Name, cancellationToken);
                var tick = _harness.WaitForTickCountAsync(grain.GetGrainId(), Name, 1, cancellationToken);
                await _harness.AdvanceAsync(_harness.ReminderLoadingWindow, cancellationToken);
                await tick;
                await AssertPersistedAsync(Guarantee, grain.GetGrainId(), Name, expectedStart, Period, cancellationToken);
                AssertCounts(Guarantee, grain, Name, owners: null, ticks: 1);
            },
            cleanupToken => CleanupAsync(Guarantee, grain, Name, cleanupToken));
    }

    /// <summary>Guarantee: registration and ownership reconcile to one owner after a silo joins.</summary>
    public virtual Task ReminderService_StaleOwnerRegistrationReconciles()
        => RunReminderService_StaleOwnerRegistrationReconciles(CancellationToken.None);

    /// <summary>Runs stale-owner registration reconciliation.</summary>
    public async Task RunReminderService_StaleOwnerRegistrationReconciles(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderService_StaleOwnerRegistrationReconciles);
        var reminders = CreateGrains(Guarantee, 16);
        var phase = "join";
        SiloAddress? joined = null;
        await ExecuteWithCleanupAsync(
            Guarantee,
            async () =>
            {
                try
                {
                    joined = await _harness.JoinOneSiloAsync(cancellationToken);
                    phase = "registration";
                    foreach (var (grain, name) in reminders)
                    {
                        await grain.RegisterOrUpdateAsync(name, TimeSpan.FromSeconds(3), Period).WaitAsync(cancellationToken);
                    }

                    phase = "topology reconciliation";
                    await _harness.WaitForTopologyReconciliationAsync(cancellationToken);
                    phase = "owner reconciliation";
                    var ownerWaits = reminders
                        .Select(item => _harness.WaitForOwnerCountAsync(
                            item.Grain.GetGrainId(),
                            item.Name,
                            1,
                            cancellationToken))
                        .ToArray();
                    await _harness.AdvanceAsync(_harness.ReminderRefreshPeriod, cancellationToken);
                    await Task.WhenAll(ownerWaits);
                    foreach (var (grain, name) in reminders)
                    {
                        phase = $"schedule reconciliation for {grain.GetGrainId()}/{name}";
                        await _harness.WaitForScheduleAsync(grain.GetGrainId(), name, cancellationToken);
                        if (_harness.GetOwners(grain.GetGrainId(), name).Count != 1)
                        {
                            OwnershipFailure(Guarantee, grain.GetGrainId(), name, 1).Throw();
                        }
                    }
                }
                catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
                {
                    Fail(Guarantee, phase)
                        .WithExpected("all registrations reconcile to one current owner with an armed schedule")
                        .WithObserved(string.Join(
                            "; ",
                            reminders.Select(item =>
                                $"{item.Grain.GetGrainId()}/{item.Name}="
                                + $"[{string.Join(", ", _harness.GetOwners(item.Grain.GetGrainId(), item.Name))}]")))
                        .Throw(exception);
                }
            },
            async cleanupToken =>
            {
                if (joined is not null && _harness.ActiveSilos.Contains(joined))
                {
                    await _harness.LeaveSiloAsync(joined, cleanupToken);
                    await _harness.WaitForTopologyReconciliationAsync(cleanupToken);
                }

                await CleanupAsync(Guarantee, reminders, cleanupToken);
            });
    }

    /// <summary>Guarantee: one-silo join/leave transfers ownership without duplicates or missed delivery.</summary>
    public virtual Task ReminderService_OneSiloJoinLeaveTransfersOwnership()
        => RunReminderService_OneSiloJoinLeaveTransfersOwnership(CancellationToken.None);

    /// <summary>Runs the one-silo join/leave ownership-transfer scenario.</summary>
    public async Task RunReminderService_OneSiloJoinLeaveTransfersOwnership(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderService_OneSiloJoinLeaveTransfersOwnership);
        var reminders = CreateGrains(Guarantee, 32);
        var initialOwners = new Dictionary<GrainId, SiloAddress>();
        SiloAddress? joined = null;
        await ExecuteWithCleanupAsync(
            Guarantee,
            async () =>
            {
                foreach (var (grain, name) in reminders)
                {
                    await grain.RegisterOrUpdateAsync(name, TimeSpan.FromSeconds(3), Period).WaitAsync(cancellationToken);
                    await _harness.WaitForOwnerCountAsync(grain.GetGrainId(), name, 1, cancellationToken);
                    initialOwners[grain.GetGrainId()] = _harness.GetOwners(grain.GetGrainId(), name).Single();
                }

                joined = await _harness.JoinOneSiloAsync(cancellationToken);
                await _harness.WaitForTopologyReconciliationAsync(cancellationToken);
                var transferred = 0;
                foreach (var (grain, name) in reminders)
                {
                    await _harness.WaitForOwnerCountAsync(grain.GetGrainId(), name, 1, cancellationToken);
                    var owner = _harness.GetOwners(grain.GetGrainId(), name).Single();
                    transferred += owner.Equals(joined) ? 1 : 0;
                }

                if (transferred == 0)
                {
                    Fail(Guarantee, "join reconciliation")
                        .WithExpected($"at least one of {reminders.Count} deterministic reminders transferred to {joined}")
                        .WithObserved("all reminders remained on the initial silos")
                        .Throw();
                }

                await _harness.LeaveSiloAsync(joined, cancellationToken);
                joined = null;
                await _harness.WaitForTopologyReconciliationAsync(cancellationToken);
                foreach (var (grain, name) in reminders)
                {
                    await _harness.WaitForOwnerCountAsync(grain.GetGrainId(), name, 1, cancellationToken);
                    var owner = _harness.GetOwners(grain.GetGrainId(), name).Single();
                    var expectedOwner = initialOwners[grain.GetGrainId()];
                    if (!owner.Equals(expectedOwner))
                    {
                        OwnershipFailure(Guarantee, grain.GetGrainId(), name, 1)
                            .WithExpected($"owner={expectedOwner} after joined silo leaves")
                            .WithObserved($"owner={owner}")
                            .Throw();
                    }

                    await _harness.WaitForScheduleAsync(grain.GetGrainId(), name, cancellationToken);
                }

                var tickWaits = reminders
                    .Select(item => _harness.WaitForTickCountAsync(
                        item.Grain.GetGrainId(),
                        item.Name,
                        1,
                        cancellationToken))
                    .ToArray();
                await _harness.AdvanceAsync(TimeSpan.FromSeconds(3), cancellationToken);
                await Task.WhenAll(tickWaits);
                foreach (var (grain, name) in reminders)
                {
                    AssertCounts(Guarantee, grain, name, owners: null, ticks: 1);
                }
            },
            async cleanupToken =>
            {
                if (joined is not null && _harness.ActiveSilos.Contains(joined))
                {
                    await _harness.LeaveSiloAsync(joined, cleanupToken);
                    await _harness.WaitForTopologyReconciliationAsync(cleanupToken);
                }

                await CleanupAsync(Guarantee, reminders, cleanupToken);
            });
    }

    /// <summary>Guarantee: scenario cleanup removes only rows owned by that scenario.</summary>
    public virtual Task ReminderService_CleanupIsIsolated()
        => RunReminderService_CleanupIsIsolated(CancellationToken.None);

    /// <summary>Runs cleanup isolation.</summary>
    public async Task RunReminderService_CleanupIsIsolated(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderService_CleanupIsIsolated);
        var sentinel = CreateGrain($"{Guarantee}/sentinel");
        var subject = CreateGrain($"{Guarantee}/subject");
        const string SentinelName = "unrelated-sentinel";
        const string SubjectName = "scenario-owned";
        await ExecuteWithCleanupAsync(
            Guarantee,
            async () =>
            {
                await sentinel.RegisterOrUpdateAsync(SentinelName, TimeSpan.FromMinutes(1), Period).WaitAsync(cancellationToken);
                await subject.RegisterOrUpdateAsync(SubjectName, TimeSpan.FromMinutes(1), Period).WaitAsync(cancellationToken);
                await CleanupAsync(Guarantee, subject, SubjectName, cancellationToken);
                var sentinelRow = await _harness.ReminderTable.ReadRow(sentinel.GetGrainId(), SentinelName).WaitAsync(cancellationToken);
                if (sentinelRow is null)
                {
                    Fail(Guarantee, "cleanup")
                        .WithIdentity(sentinel.GetGrainId(), SentinelName)
                        .WithExpected("unrelated sentinel remains registered")
                        .WithObserved("sentinel row was removed")
                        .Throw();
                }
            },
            async cleanupToken =>
            {
                await CleanupAsync(Guarantee, sentinel, SentinelName, cleanupToken);
                await CleanupAsync(Guarantee, subject, SubjectName, cleanupToken);
            });
    }

    private List<(IReminderServiceTestGrain Grain, string Name)> CreateGrains(string guarantee, int count)
        => Enumerable.Range(0, count)
            .Select(index => (CreateGrain($"{guarantee}/{index}"), $"churn-{index.ToString("D2", CultureInfo.InvariantCulture)}"))
            .ToList();

    private IReminderServiceTestGrain CreateGrain(string label)
    {
        var ordinal = Interlocked.Increment(ref _grainCounter);
        var key = ReminderTestData.CreateGuid(_seed, $"{ProviderName}/{label}/{ordinal}");
        return _harness.GrainFactory.GetGrain<IReminderServiceTestGrain>(key);
    }

    private async Task ExecuteWithCleanupAsync(
        string guarantee,
        Func<Task> scenario,
        Func<CancellationToken, Task> cleanup)
    {
        ExceptionDispatchInfo? scenarioFailure = null;
        try
        {
            await scenario();
        }
        catch (Exception exception)
        {
            scenarioFailure = ExceptionDispatchInfo.Capture(exception);
        }

        Exception? cleanupFailure = null;
        using (var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try
            {
                await cleanup(cleanupCancellation.Token);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
        }

        if (scenarioFailure is not null)
        {
            if (cleanupFailure is not null)
            {
                scenarioFailure.SourceException.Data["ReminderServiceLifecycleCleanupFailure"] = cleanupFailure.ToString();
            }

            scenarioFailure.Throw();
        }

        if (cleanupFailure is not null)
        {
            Fail(guarantee, "scenario cleanup")
                .WithExpected("all scenario-owned reminders absent and all temporary topology restored")
                .WithObserved($"{cleanupFailure.GetType().FullName}: {cleanupFailure.Message}")
                .Throw(cleanupFailure);
        }
    }

    private async Task CleanupAsync(
        string guarantee,
        IReadOnlyList<(IReminderServiceTestGrain Grain, string Name)> reminders,
        CancellationToken cancellationToken)
    {
        foreach (var (grain, name) in reminders)
        {
            await CleanupAsync(guarantee, grain, name, cancellationToken);
        }
    }

    private async Task CleanupAsync(
        string guarantee,
        IReminderServiceTestGrain grain,
        string name,
        CancellationToken cancellationToken)
    {
        if (await grain.UnregisterAsync(name).WaitAsync(cancellationToken))
        {
            await _harness.WaitForOwnerCountAsync(grain.GetGrainId(), name, 0, cancellationToken);
        }

        await AssertAbsentAsync(guarantee, grain.GetGrainId(), name, cancellationToken);
    }

    private async Task AssertPersistedAsync(
        string guarantee,
        GrainId grainId,
        string name,
        DateTime expectedStart,
        TimeSpan expectedPeriod,
        CancellationToken cancellationToken)
    {
        var row = await ReadRequiredAsync(guarantee, grainId, name, cancellationToken);
        if (row.StartAt != expectedStart || row.Period != expectedPeriod || string.IsNullOrEmpty(row.ETag))
        {
            Fail(guarantee, "ReadRow")
                .WithIdentity(grainId, name)
                .WithExpected($"StartAt={expectedStart:O}, Period={expectedPeriod}, non-empty ETag")
                .WithObserved(Describe(row))
                .WithSchedule(row.StartAt, row.Period)
                .WithETags(row.ETag)
                .Throw();
        }
    }

    private async Task<ReminderEntry> ReadRequiredAsync(
        string guarantee,
        GrainId grainId,
        string name,
        CancellationToken cancellationToken)
    {
        var row = await _harness.ReminderTable.ReadRow(grainId, name).WaitAsync(cancellationToken);
        if (row is null)
        {
            Fail(guarantee, "ReadRow")
                .WithIdentity(grainId, name)
                .WithExpected("one persisted row")
                .WithObserved("<null>")
                .Throw();
        }

        return row!;
    }

    private async Task AssertAbsentAsync(
        string guarantee,
        GrainId grainId,
        string name,
        CancellationToken cancellationToken)
    {
        var row = await _harness.ReminderTable.ReadRow(grainId, name).WaitAsync(cancellationToken);
        if (row is not null)
        {
            Fail(guarantee, "cleanup")
                .WithIdentity(grainId, name)
                .WithExpected("row absent")
                .WithObserved(Describe(row))
                .WithETags(row.ETag)
                .Throw();
        }
    }

    private void AssertCounts(
        string guarantee,
        IReminderServiceTestGrain grain,
        string name,
        int? owners,
        int ticks)
    {
        var diagnosticTicks = _harness.GetTickCount(grain.GetGrainId(), name);
        var actualOwners = _harness.GetOwners(grain.GetGrainId(), name);
        if ((owners is { } expectedOwners && actualOwners.Count != expectedOwners)
            || diagnosticTicks != ticks)
        {
            Fail(guarantee, "exact counters")
                .WithIdentity(grain.GetGrainId(), name)
                .WithExpected($"owners={(owners?.ToString(CultureInfo.InvariantCulture) ?? "<not-asserted>")}, completedTicks={ticks}")
                .WithObserved(
                    $"owners={actualOwners.Count} [{string.Join(", ", actualOwners)}], "
                    + $"completedTicks={diagnosticTicks}")
                .Throw();
        }
    }

    private ReminderFailureReport OwnershipFailure(string guarantee, GrainId grainId, string name, int expected)
        => Fail(guarantee, "ownership reconciliation")
            .WithIdentity(grainId, name)
            .WithExpected($"exactly {expected} local owner(s)")
            .WithObserved($"owners=[{string.Join(", ", _harness.GetOwners(grainId, name))}]");

    private ReminderFailureReport Fail(string guarantee, string operation)
        => ReminderFailureReport.Create(ProviderName, guarantee, operation)
            .WithDetail("seed", _seed.ToString(CultureInfo.InvariantCulture))
            .WithDetail("clock", _harness.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            .WithDetail("activeSilos", string.Join(", ", _harness.ActiveSilos));

    private static string Describe(ReminderEntry row)
        => $"(GrainId={row.GrainId}, ReminderName='{row.ReminderName}', StartAt={row.StartAt:O}, "
            + $"Period={row.Period}, ETag='{row.ETag}')";
}
