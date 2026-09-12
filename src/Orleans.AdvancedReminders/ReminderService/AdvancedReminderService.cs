using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;


internal sealed partial class AdvancedReminderService : IReminderService, IAttributeReminderService, ILifecycleParticipant<ISiloLifecycle>
{
    internal static readonly TimeSpan RecoveryHeartbeatPeriod = TimeSpan.FromMinutes(1);
    private const string GrainIdMetadataKey = "grain-id";
    private const string ReminderNameMetadataKey = "reminder-name";
    private const string ScheduleIdMetadataKey = "schedule-id";
    private const string LegacyETagMetadataKey = "etag";
    private const string JobNamePrefix = "advanced-reminder:";
    private readonly IReminderTable _reminderTable;
    private readonly ILocalDurableJobManager _jobManager;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<AdvancedReminderService> _logger;
    private readonly ReminderOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IClusterManifestProvider _clusterManifestProvider;
    private readonly IClusterMembershipService _clusterMembershipService;
    private readonly CancellationTokenSource _recoveryMonitorCts = new();
    private Task? _recoveryMonitorTask;

    public AdvancedReminderService(
        IReminderTable reminderTable,
        ILocalDurableJobManager jobManager,
        IGrainFactory grainFactory,
        IOptions<ReminderOptions> options,
        ILogger<AdvancedReminderService> logger,
        [FromKeyedServices(DurableJobTimeProviderNames.DurableJobs)] TimeProvider timeProvider,
        IClusterManifestProvider clusterManifestProvider,
        IClusterMembershipService clusterMembershipService)
    {
        _reminderTable = reminderTable;
        _jobManager = jobManager;
        _grainFactory = grainFactory;
        _logger = logger;
        _options = options.Value;
        _timeProvider = timeProvider;
        _clusterManifestProvider = clusterManifestProvider;
        _clusterMembershipService = clusterMembershipService;
    }

    public async Task<IGrainReminder> RegisterOrUpdateReminder(
        GrainId grainId,
        string reminderName,
        ReminderSchedule schedule,
        Runtime.MissedReminderAction action)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ReminderValidation.Validate(_options, reminderName, schedule, action, GetUtcNow());

        ReminderEntry entry = schedule.Kind switch
        {
            Runtime.ReminderScheduleKind.Interval => CreateIntervalEntry(grainId, reminderName, schedule, action),
            Runtime.ReminderScheduleKind.Cron => CreateCronEntry(grainId, reminderName, schedule, action),
            _ => throw new ArgumentOutOfRangeException(nameof(schedule), schedule.Kind, "Unsupported reminder schedule kind."),
        };

        return await GetDispatcher(grainId).RegisterOrUpdateAsync(entry);
    }

    public async Task<IGrainReminder> ReconcileReminder(
        GrainId grainId,
        string reminderName,
        ReminderSchedule schedule,
        Runtime.MissedReminderAction action,
        string declarationId)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentException.ThrowIfNullOrWhiteSpace(declarationId);
        ReminderValidation.Validate(_options, reminderName, schedule, action, GetUtcNow());

        ReminderEntry entry = schedule.Kind switch
        {
            Runtime.ReminderScheduleKind.Interval => CreateIntervalEntry(grainId, reminderName, schedule, action),
            Runtime.ReminderScheduleKind.Cron => CreateCronEntry(grainId, reminderName, schedule, action),
            _ => throw new ArgumentOutOfRangeException(nameof(schedule), schedule.Kind, "Unsupported reminder schedule kind."),
        };

        return await GetDispatcher(grainId).ReconcileAttributeAsync(entry, declarationId);
    }

    public Task UnregisterReminder(IGrainReminder reminder)
    {
        if (reminder is not ReminderData data)
        {
            throw new ArgumentException("Reminder handle was not created by Orleans.AdvancedReminders.", nameof(reminder));
        }

        return GetDispatcher(data.GrainId).UnregisterAsync(data);
    }

    public async Task<IGrainReminder?> GetReminder(GrainId grainId, string reminderName)
        => (await _reminderTable.ReadRow(grainId, reminderName))?.ToIGrainReminder();

    public async Task<List<IGrainReminder>> GetReminders(GrainId grainId)
    {
        var data = await _reminderTable.ReadRows(grainId);
        var result = new List<IGrainReminder>(data.Reminders.Count);
        foreach (var entry in data.Reminders)
        {
            result.Add(entry.ToIGrainReminder());
        }

        return result;
    }

    public Task ProcessDueReminderAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        CancellationToken cancellationToken)
        => GetDispatcher(grainId).ProcessDueReminderAsync(grainId, reminderName, expectedScheduleId, 0, cancellationToken);

    internal Task ProcessDueReminderAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        CancellationToken cancellationToken,
        int durableJobDequeueCount)
        => GetDispatcher(grainId).ProcessDueReminderAsync(grainId, reminderName, expectedScheduleId, durableJobDequeueCount, cancellationToken);

    internal Task<string> UpsertAndScheduleEntryAsync(ReminderEntry entry, CancellationToken cancellationToken)
        => GetDispatcher(entry.GrainId).UpsertAndScheduleAsync(entry, cancellationToken);

    internal async Task<IGrainReminder> RegisterOrUpdateCoreAsync(ReminderEntry entry, CancellationToken cancellationToken)
    {
        var previous = await _reminderTable.ReadRow(entry.GrainId, entry.ReminderName);
        entry.ETag = previous?.ETag ?? string.Empty;
        PrepareNewSchedule(entry);
        await PersistAndScheduleCoreAsync(entry, cancellationToken);
        if (previous is not null)
        {
            await CancelScheduledJobAsync(previous);
        }

        return entry.ToIGrainReminder();
    }

    internal async Task<IGrainReminder> ReconcileAttributeCoreAsync(
        ReminderEntry entry,
        string declarationId,
        CancellationToken cancellationToken)
    {
        var previous = await _reminderTable.ReadRow(entry.GrainId, entry.ReminderName);
        if (previous is not null && HasAttributeDeclaration(previous.ScheduleId, declarationId))
        {
            await EnsureScheduledCoreAsync(
                previous.GrainId,
                previous.ReminderName,
                previous.ScheduleId,
                force: false,
                cancellationToken);
            return previous.ToIGrainReminder();
        }

        entry.ETag = previous?.ETag ?? string.Empty;
        PrepareNewSchedule(entry, declarationId);
        await PersistAndScheduleCoreAsync(entry, cancellationToken);
        if (previous is not null)
        {
            await CancelScheduledJobAsync(previous);
        }

        return entry.ToIGrainReminder();
    }

    internal async Task<string> UpsertAndScheduleCoreAsync(ReminderEntry entry, CancellationToken cancellationToken)
    {
        var previous = await _reminderTable.ReadRow(entry.GrainId, entry.ReminderName);
        if (previous is not null && !string.Equals(previous.ETag, entry.ETag, StringComparison.Ordinal))
        {
            throw new Runtime.ReminderException($"Could not update reminder '{entry.ReminderName}' for grain '{entry.GrainId}' due to ETag mismatch.");
        }

        PrepareNewSchedule(entry);
        await PersistAndScheduleCoreAsync(entry, cancellationToken);
        if (previous is not null)
        {
            await CancelScheduledJobAsync(previous);
        }

        return entry.ETag;
    }

    internal async Task UnregisterCoreAsync(ReminderData data, CancellationToken cancellationToken)
    {
        var current = await _reminderTable.ReadRow(data.GrainId, data.ReminderName);
        if (current is null)
        {
            return;
        }

        if (!string.Equals(
                ReminderScheduleId.GetRegistrationId(current.ScheduleId),
                data.RegistrationId,
                StringComparison.Ordinal)
            || !await _reminderTable.RemoveRow(data.GrainId, data.ReminderName, current.ETag))
        {
            throw new Runtime.ReminderException($"Could not unregister reminder {data} due to ETag mismatch.");
        }

        await CancelScheduledJobAsync(current);
    }

    private DateTime GetUtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private IAdvancedReminderDispatcherGrain GetDispatcher(GrainId grainId)
        => _grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString());

    internal static bool TryGetReminderMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        out GrainId grainId,
        out string reminderName,
        out string? scheduleId)
    {
        grainId = default;
        reminderName = string.Empty;
        scheduleId = null;

        if (metadata is null
            || !metadata.TryGetValue(GrainIdMetadataKey, out var grainIdText)
            || !metadata.TryGetValue(ReminderNameMetadataKey, out var rawReminderName))
        {
            return false;
        }

        reminderName = rawReminderName;
        if (!GrainId.TryParse(grainIdText, out grainId))
        {
            return false;
        }
        if (!metadata.TryGetValue(ScheduleIdMetadataKey, out scheduleId))
        {
            metadata.TryGetValue(LegacyETagMetadataKey, out scheduleId);
        }

        return !string.IsNullOrWhiteSpace(reminderName);
    }
}
