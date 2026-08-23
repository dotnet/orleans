using System;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Reminders.TestKit;

/// <summary>
/// The grain contract used by <see cref="ReminderServiceTestRunner"/> to exercise the reminder service through its
/// public grain-facing API.
/// </summary>
public interface IReminderServiceTestGrain : IGrainWithGuidKey
{
    /// <summary>Registers or updates a reminder.</summary>
    Task<string> RegisterOrUpdateAsync(string reminderName, TimeSpan dueTime, TimeSpan period);

    /// <summary>Returns the names of all reminders registered by this grain.</summary>
    Task<string[]> GetReminderNamesAsync();

    /// <summary>Unregisters a reminder, returning <see langword="false"/> when it does not exist.</summary>
    Task<bool> UnregisterAsync(string reminderName);
}

/// <summary>
/// A framework-neutral, cluster-level conformance runner for the grain-facing reminder service.
/// </summary>
/// <remarks>
/// Provider suites host their <see cref="IReminderTable"/> in an <see cref="TestingHost.InProcessTestCluster"/>,
/// derive from this type, and apply their test framework's attributes to overrides. The runner verifies the
/// service-to-table integration independently from the direct table runner.
/// </remarks>
public abstract class ReminderServiceTestRunner
{
    private readonly int _seed;
    private int _grainCounter;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReminderServiceTestRunner"/> class.
    /// </summary>
    /// <param name="grainFactory">The deployed cluster's grain factory.</param>
    /// <param name="reminderTable">The provider resolved from the deployed cluster.</param>
    /// <param name="providerName">The provider name used in failures.</param>
    /// <param name="seed">The deterministic identity seed.</param>
    protected ReminderServiceTestRunner(
        IGrainFactory grainFactory,
        IReminderTable reminderTable,
        string providerName,
        int seed = 0)
    {
        GrainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
        ReminderTable = reminderTable ?? throw new ArgumentNullException(nameof(reminderTable));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ProviderName = providerName;
        _seed = seed;
    }

    /// <summary>Gets the deployed cluster's grain factory.</summary>
    protected IGrainFactory GrainFactory { get; }

    /// <summary>Gets the reminder table used by the deployed reminder service.</summary>
    protected IReminderTable ReminderTable { get; }

    /// <summary>Gets the provider name used in failures.</summary>
    protected string ProviderName { get; }

    /// <summary>
    /// Guarantee: registration is visible through lookup and enumeration, and unregister is explicitly observed by
    /// both the service and the table.
    /// </summary>
    public virtual async Task ReminderService_RegisterLookupEnumerateAndUnregister()
    {
        const string Guarantee = nameof(ReminderService_RegisterLookupEnumerateAndUnregister);
        var grain = CreateGrain(Guarantee);
        var grainId = grain.GetGrainId();
        const string Name = "service-lifecycle";
        var period = TimeSpan.FromMinutes(5);

        var registeredName = await grain.RegisterOrUpdateAsync(Name, period, period);
        var registrationState = await ReminderTableRetryPolicy.ReadUntilAsync(
            async () => (
                Persisted: await ReminderTable.ReadRow(grainId, Name),
                Names: await grain.GetReminderNamesAsync()),
            state => state.Persisted is { } persisted
                && persisted.GrainId == grainId
                && persisted.ReminderName == Name
                && persisted.Period == period
                && !string.IsNullOrEmpty(persisted.ETag)
                && state.Names.SequenceEqual([Name], StringComparer.Ordinal),
            ProviderName,
            Guarantee,
            "RegisterOrUpdateReminder/Read",
            $"one persisted row and one enumerated reminder named '{Name}'",
            state => $"names=[{string.Join(", ", state.Names)}], row={Describe(state.Persisted)}");
        var persisted = registrationState.Persisted;
        var names = registrationState.Names;

        if (registeredName != Name
            || persisted is null
            || persisted.GrainId != grainId
            || persisted.ReminderName != Name
            || persisted.Period != period
            || string.IsNullOrEmpty(persisted.ETag)
            || !names.SequenceEqual([Name], StringComparer.Ordinal))
        {
            Failure(Guarantee, "RegisterOrUpdateReminder")
                .WithIdentity(grainId, Name)
                .WithExpected($"registeredName='{Name}', one enumerated reminder, Period={period}, and a non-empty ETag")
                .WithObserved($"registeredName='{registeredName}', names=[{string.Join(", ", names)}], row={Describe(persisted)}")
                .WithETags(persisted?.ETag)
                .Throw();
        }

        var removed = await grain.UnregisterAsync(Name);
        var removedAgain = await grain.UnregisterAsync(Name);
        var removalState = await ReminderTableRetryPolicy.ReadUntilAsync(
            async () => (
                Persisted: await ReminderTable.ReadRow(grainId, Name),
                Names: await grain.GetReminderNamesAsync()),
            state => state.Persisted is null && state.Names.Length == 0,
            ProviderName,
            Guarantee,
            "UnregisterReminder/Read",
            "no persisted or enumerated reminder after unregister",
            state => $"names=[{string.Join(", ", state.Names)}], row={Describe(state.Persisted)}");
        var afterRemoval = removalState.Persisted;
        var namesAfterRemoval = removalState.Names;
        if (!removed || removedAgain || afterRemoval is not null || namesAfterRemoval.Length != 0)
        {
            Failure(Guarantee, "UnregisterReminder")
                .WithIdentity(grainId, Name)
                .WithExpected("first removal=true, repeated removal=false, point read=null, enumeration=[]")
                .WithObserved($"first={removed}, repeated={removedAgain}, row={Describe(afterRemoval)}, names=[{string.Join(", ", namesAfterRemoval)}]")
                .WithETags(afterRemoval?.ETag, supplied: persisted?.ETag)
                .Throw();
        }
    }

    /// <summary>
    /// Guarantee: updating an existing service reminder replaces its schedule and ETag without duplicating its
    /// identity.
    /// </summary>
    public virtual async Task ReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate()
    {
        const string Guarantee = nameof(ReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate);
        var grain = CreateGrain(Guarantee);
        var grainId = grain.GetGrainId();
        const string Name = "service-update";

        await grain.RegisterOrUpdateAsync(Name, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        var original = await ReminderTableRetryPolicy.ReadUntilAsync(
            () => ReminderTable.ReadRow(grainId, Name),
            entry => entry is not null && !string.IsNullOrEmpty(entry.ETag),
            ProviderName,
            Guarantee,
            "RegisterOrUpdateReminder/ReadOriginal",
            "the initially registered reminder with a non-empty ETag",
            Describe);
        var originalEntry = original!;
        await grain.RegisterOrUpdateAsync(Name, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(9));
        var updateState = await ReminderTableRetryPolicy.ReadUntilAsync(
            async () =>
            {
                var updated = await ReminderTable.ReadRow(grainId, Name);
                if (updated is not null
                    && updated.StartAt != originalEntry.StartAt
                    && updated.Period == TimeSpan.FromMinutes(9)
                    && string.Equals(updated.ETag, originalEntry.ETag, StringComparison.Ordinal))
                {
                    Failure(Guarantee, "RegisterOrUpdateReminder")
                        .WithIdentity(grainId, Name)
                        .WithExpected($"a replacement ETag different from '{originalEntry.ETag}'")
                        .WithObserved($"the updated schedule is visible with the reused ETag '{updated.ETag}'")
                        .WithETags(updated.ETag, originalEntry.ETag)
                        .Throw();
                }

                return (
                    Updated: updated,
                    Rows: await ReminderTable.ReadRows(grainId));
            },
            state => state.Updated is { } updated
                && !string.IsNullOrEmpty(updated.ETag)
                && updated.ETag != originalEntry.ETag
                && updated.StartAt != originalEntry.StartAt
                && updated.Period == TimeSpan.FromMinutes(9)
                && state.Rows.Reminders.Count == 1
                && state.Rows.Reminders[0] is { } enumerated
                && enumerated.GrainId == grainId
                && enumerated.ReminderName == Name
                && enumerated.StartAt == updated.StartAt
                && enumerated.Period == updated.Period
                && enumerated.ETag == updated.ETag,
            ProviderName,
            Guarantee,
            "RegisterOrUpdateReminder/ReadUpdated",
            "one exact updated row with a changed StartAt, Period=00:09:00, and a new ETag",
            state => $"updated={Describe(state.Updated)}, rowCount={state.Rows.Reminders.Count}, enumerated={Describe(state.Rows.Reminders.Count == 1 ? state.Rows.Reminders[0] : null)}");
        var updated = updateState.Updated;
        var rows = updateState.Rows;
        var enumerated = rows.Reminders.Count == 1 ? rows.Reminders[0] : null;

        if (original is null
            || updated is null
            || string.IsNullOrEmpty(original.ETag)
            || string.IsNullOrEmpty(updated.ETag)
            || original.ETag == updated.ETag
            || original.StartAt == updated.StartAt
            || updated.Period != TimeSpan.FromMinutes(9)
            || enumerated is null
            || enumerated.GrainId != grainId
            || enumerated.ReminderName != Name
            || enumerated.StartAt != updated.StartAt
            || enumerated.Period != updated.Period
            || enumerated.ETag != updated.ETag)
        {
            Failure(Guarantee, "RegisterOrUpdateReminder")
                .WithIdentity(grainId, Name)
                .WithExpected("one exact row with a changed StartAt, Period=00:09:00, and an ETag different from the original")
                .WithObserved(
                    $"original={Describe(original)}, updated={Describe(updated)}, rowCount={rows.Reminders.Count}, enumerated={Describe(enumerated)}")
                .WithETags(updated?.ETag, originalEntry.ETag)
                .Throw();
        }

        await grain.UnregisterAsync(Name);
    }

    private IReminderServiceTestGrain CreateGrain(string label)
    {
        var ordinal = ++_grainCounter;
        var key = ReminderTestData.CreateGuid(_seed, $"{ProviderName}/{label}/{ordinal}");
        return GrainFactory.GetGrain<IReminderServiceTestGrain>(key);
    }

    private ReminderFailureReport Failure(string guarantee, string operation)
        => ReminderFailureReport.Create(ProviderName, guarantee, operation)
            .WithDetail("seed", _seed.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string Describe(ReminderEntry? entry)
        => entry is null
            ? "<null>"
            : $"(GrainId={entry.GrainId}, ReminderName='{entry.ReminderName}', StartAt={entry.StartAt:O}, Period={entry.Period}, ETag='{entry.ETag}')";
}

internal sealed class ReminderServiceTestGrain : Grain, IReminderServiceTestGrain, IRemindable
{
    public async Task<string> RegisterOrUpdateAsync(string reminderName, TimeSpan dueTime, TimeSpan period)
        => (await this.RegisterOrUpdateReminder(reminderName, dueTime, period)).ReminderName;

    public async Task<string[]> GetReminderNamesAsync()
        => [.. (await this.GetReminders()).Select(reminder => reminder.ReminderName).Order(StringComparer.Ordinal)];

    public async Task<bool> UnregisterAsync(string reminderName)
    {
        var reminder = await this.GetReminder(reminderName);
        if (reminder is null)
        {
            return false;
        }

        await this.UnregisterReminder(reminder);
        return true;
    }

    public Task ReceiveReminder(string reminderName, TickStatus status) => Task.CompletedTask;
}
