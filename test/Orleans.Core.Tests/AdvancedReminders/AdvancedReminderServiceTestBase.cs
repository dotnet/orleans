#nullable enable
using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.DurableJobs;
using Orleans.Metadata;
using Xunit;
using AdvancedRemindable = Orleans.AdvancedReminders.IRemindable;
using AdvancedReminderOptions = Orleans.AdvancedReminders.ReminderOptions;
using AdvancedTickStatus = Orleans.AdvancedReminders.Runtime.TickStatus;
using IGrainReminder = Orleans.AdvancedReminders.IGrainReminder;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using ReminderTableData = Orleans.AdvancedReminders.ReminderTableData;

namespace UnitTests.AdvancedReminders;

public abstract class AdvancedReminderServiceTestBase
{
    private protected static AdvancedReminderService CreateService(
        Orleans.AdvancedReminders.IReminderTable reminderTable,
        AdvancedReminderOptions? options = null,
        ILocalDurableJobManager? jobManager = null,
        IGrainFactory? grainFactory = null,
        TimeProvider? timeProvider = null,
        IClusterManifestProvider? clusterManifestProvider = null,
        IClusterMembershipService? clusterMembershipService = null)
    {
        jobManager ??= Substitute.For<ILocalDurableJobManager>();
        grainFactory ??= Substitute.For<IGrainFactory>();
        return new AdvancedReminderService(
            reminderTable,
            jobManager,
            grainFactory,
            Options.Create(options ?? new AdvancedReminderOptions()),
            NullLogger<AdvancedReminderService>.Instance,
            timeProvider ?? TimeProvider.System,
            clusterManifestProvider ?? Substitute.For<IClusterManifestProvider>(),
            clusterMembershipService ?? Substitute.For<IClusterMembershipService>());
    }

    private protected static ReminderEntry CreateDueEntry(DateTimeOffset now, string key)
        => new()
        {
            GrainId = GrainId.Create("test", key),
            ReminderName = "recurring",
            StartAt = now.UtcDateTime.AddMinutes(-5),
            NextDueUtc = now.UtcDateTime,
            Period = TimeSpan.FromMinutes(5),
            ETag = $"etag-{key}",
            ScheduleId = $"schedule-{key}",
        };

    private protected static IJobRunContext CreateReminderJobContext(ReminderEntry entry, int dequeueCount)
    {
        var context = Substitute.For<IJobRunContext>();
        context.DequeueCount.Returns(dequeueCount);
        context.Job.Returns(new DurableJob
        {
            Id = $"job-{entry.GrainId.Key}",
            Name = $"advanced-reminder:{entry.ReminderName}",
            DueTime = new DateTimeOffset(entry.NextDueUtc ?? entry.StartAt, TimeSpan.Zero),
            TargetGrainId = GrainId.Create("advanced-reminder-dispatcher", entry.GrainId.ToString()),
            ShardId = "test-shard",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grain-id"] = entry.GrainId.ToString(),
                ["reminder-name"] = entry.ReminderName,
                ["schedule-id"] = entry.ScheduleId,
            },
        });
        return context;
    }

    private protected static (IClusterManifestProvider ManifestProvider, IClusterMembershipService MembershipService) CreateClusterState(
        params GrainType[] grainTypes)
    {
        var grainProperties = new GrainProperties(ImmutableDictionary.Create<string, string>(StringComparer.Ordinal));
        var grains = grainTypes.ToImmutableDictionary(static grainType => grainType, _ => grainProperties);
        var manifest = new GrainManifest(
            grains,
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        var siloAddress = SiloAddress.New(System.Net.IPAddress.Loopback, 11111, 1);
        var clusterManifest = new ClusterManifest(
            new MajorMinorVersion(1, 0),
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty.Add(siloAddress, manifest));
        var membership = new ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(
                siloAddress,
                new ClusterMember(siloAddress, SiloStatus.Active, "silo-1")),
            new MembershipVersion(1));
        return CreateClusterState(clusterManifest, membership);
    }

    private protected static (IClusterManifestProvider ManifestProvider, IClusterMembershipService MembershipService) CreateIncompleteClusterState()
    {
        var localSilo = SiloAddress.New(System.Net.IPAddress.Loopback, 11111, 1);
        var remoteSilo = SiloAddress.New(System.Net.IPAddress.Loopback, 11112, 1);
        var emptyManifest = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty,
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        var clusterManifest = new ClusterManifest(
            new MajorMinorVersion(1, 0),
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty.Add(localSilo, emptyManifest));
        var membership = new ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty
                .Add(localSilo, new ClusterMember(localSilo, SiloStatus.Active, "silo-1"))
                .Add(remoteSilo, new ClusterMember(remoteSilo, SiloStatus.Active, "silo-2")),
            new MembershipVersion(1));
        return CreateClusterState(clusterManifest, membership);
    }

    private protected static (IClusterManifestProvider ManifestProvider, IClusterMembershipService MembershipService) CreateClusterState(
        ClusterManifest clusterManifest,
        ClusterMembershipSnapshot membership)
    {
        var manifestProvider = Substitute.For<IClusterManifestProvider>();
        manifestProvider.Current.Returns(clusterManifest);
        var membershipService = Substitute.For<IClusterMembershipService>();
        membershipService.CurrentSnapshot.Returns(membership);
        return (manifestProvider, membershipService);
    }

    private protected static DurableJob CreateDurableJob(ScheduleJobRequest request)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.JobName,
            DueTime = request.DueTime,
            TargetGrainId = request.Target,
            ShardId = "test-shard",
            Metadata = request.Metadata,
        };

    private protected static TestAdvancedReminderDispatcherGrain CreateDispatcherGrain(GrainId grainId)
        => new TestAdvancedReminderDispatcherGrain(grainId);

    private protected static async Task ExecuteScheduledReminderAfterAdvancingTimeAsync(
        AdvancedReminderService service,
        FakeTimeProvider timeProvider,
        ScheduleJobRequest request,
        CallbackRemindable remindable)
    {
        var queue = new InMemoryJobQueue(timeProvider);
        queue.Enqueue(CreateDurableJob(request), dequeueCount: 0);
        queue.MarkAsComplete();
        await using var enumerator = queue.GetAsyncEnumerator();
        var dequeueTask = enumerator.MoveNextAsync().AsTask();

        Assert.False(dequeueTask.IsCompleted);
        Assert.Empty(remindable.ReceivedStatuses);

        var advanceBy = request.DueTime - timeProvider.GetUtcNow();
        Assert.True(advanceBy > TimeSpan.Zero);
        timeProvider.Advance(advanceBy);

        Assert.True(await dequeueTask.WaitAsync(TimeSpan.FromSeconds(5)));
        var dispatcher = new AdvancedReminderDispatcherGrain(service);
        await dispatcher.ExecuteJobAsync(enumerator.Current, CancellationToken.None);
    }

    private protected static ReminderEntry Clone(
        ReminderEntry entry,
        string? etag = null,
        string? cronExpression = null,
        string? cronTimeZoneId = null,
        DateTime? nextDueUtc = null,
        DateTime? lastFireUtc = null,
        string? scheduleId = null)
        => new()
        {
            GrainId = entry.GrainId,
            ReminderName = entry.ReminderName,
            StartAt = entry.StartAt,
            Period = entry.Period,
            ETag = etag ?? entry.ETag,
            CronExpression = cronExpression ?? entry.CronExpression,
            CronTimeZoneId = cronTimeZoneId ?? entry.CronTimeZoneId,
            NextDueUtc = nextDueUtc ?? entry.NextDueUtc,
            LastFireUtc = lastFireUtc ?? entry.LastFireUtc,
            Action = entry.Action,
            ScheduleId = scheduleId ?? entry.ScheduleId,
            JobId = entry.JobId,
            JobShardId = entry.JobShardId,
        };

    private protected static void AssertReminderReceived(AdvancedRemindable remindable, string reminderName, Action<AdvancedTickStatus> assertStatus)
    {
        var receiveCalls = remindable.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(AdvancedRemindable.ReceiveReminder))
            .ToArray();

        var call = Assert.Single(receiveCalls);
        var arguments = call.GetArguments();
        Assert.Equal(reminderName, Assert.IsType<string>(arguments[0]));
        assertStatus(Assert.IsType<AdvancedTickStatus>(arguments[1]));
    }

    private protected sealed class MutableReminderTable : Orleans.AdvancedReminders.IReminderTable
    {
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        private ReminderEntry? _current;

        public MutableReminderTable(ReminderEntry? current) => _current = current is null ? null : Clone(current);

        public int UpsertCount { get; private set; }

        public int FailUpsertCall { get; init; }

        public List<(string ETag, bool Removed)> RemoveAttempts { get; } = new();

        public void ReplaceCurrent(ReminderEntry? entry) => _current = entry is null ? null : Clone(entry);

        public void DeleteCurrent() => _current = null;

        public Task<ReminderTableData> ReadRows(GrainId grainId)
            => Task.FromResult(_current is not null && _current.GrainId == grainId
                ? new ReminderTableData([Clone(_current)])
                : new ReminderTableData());

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
            => Task.FromResult(_current is null ? new ReminderTableData() : new ReminderTableData([Clone(_current)]));

        public Task<ReminderTableData> ReadRows(uint begin, uint end, int maxRows, string? continuationToken)
            => Task.FromResult(
                continuationToken is null && _current is not null
                    ? new ReminderTableData([Clone(_current)])
                    : new ReminderTableData());

        public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
            => Task.FromResult(_current is not null && _current.GrainId == grainId && _current.ReminderName == reminderName
                ? Clone(_current)
                : null);

        public Task<string> UpsertRow(ReminderEntry entry)
        {
            UpsertCount++;
            if (UpsertCount == FailUpsertCall)
            {
                throw new InvalidOperationException("injected reminder table upsert failure");
            }

            var updated = Clone(entry, etag: $"{entry.ETag}-next");
            _current = updated;
            return Task.FromResult(updated.ETag);
        }

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            var removed = _current is not null
                && _current.GrainId == grainId
                && _current.ReminderName == reminderName
                && string.Equals(_current.ETag, eTag, StringComparison.Ordinal);
            RemoveAttempts.Add((eTag, removed));
            if (removed)
            {
                _current = null;
            }

            return Task.FromResult(removed);
        }

        public Task TestOnlyClearTable()
        {
            _current = null;
            return Task.CompletedTask;
        }
    }

    private protected sealed class CallbackRemindable(Func<Task> onReminder) : AdvancedRemindable
    {
        public List<AdvancedTickStatus> ReceivedStatuses { get; } = new();

        public async Task ReceiveReminder(string reminderName, AdvancedTickStatus status)
        {
            ReceivedStatuses.Add(status);
            await onReminder();
        }
    }

    private protected sealed class TestAdvancedReminderDispatcherGrain : IAdvancedReminderDispatcherGrain, IGrainBase
    {
        public TestAdvancedReminderDispatcherGrain(GrainId grainId)
        {
            var context = Substitute.For<IGrainContext>();
            context.GrainId.Returns(grainId);
            GrainContext = context;
        }

        public IGrainContext GrainContext { get; }

        public AdvancedReminderService? Service { get; set; }

        public Task<IGrainReminder> RegisterOrUpdateAsync(ReminderEntry entry)
            => Service!.RegisterOrUpdateCoreAsync(entry, CancellationToken.None);

        public Task<IGrainReminder> ReconcileAttributeAsync(ReminderEntry entry, string declarationId)
            => Service!.ReconcileAttributeCoreAsync(entry, declarationId, CancellationToken.None);

        public Task<string> UpsertAndScheduleAsync(ReminderEntry entry, CancellationToken cancellationToken)
            => Service!.UpsertAndScheduleCoreAsync(entry, cancellationToken);

        public Task UnregisterAsync(Orleans.AdvancedReminders.ReminderData reminder)
            => Service!.UnregisterCoreAsync(reminder, CancellationToken.None);

        public Task ProcessDueReminderAsync(GrainId grainId, string reminderName, string? expectedScheduleId, int durableJobDequeueCount, CancellationToken cancellationToken)
            => Service!.ProcessDueReminderCoreAsync(grainId, reminderName, expectedScheduleId, cancellationToken, durableJobDequeueCount);

        public Task EnsureScheduledAsync(GrainId grainId, string reminderName, string? expectedScheduleId, bool force, CancellationToken cancellationToken)
            => Service!.EnsureScheduledCoreAsync(grainId, reminderName, expectedScheduleId, force, cancellationToken);

        public Task ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
