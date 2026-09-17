using System.Collections;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.TestingHost.Diagnostics;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class ProcessedRetentionMaintenanceTests : DurableMessagingBehaviorTestBase
{
    public ProcessedRetentionMaintenanceTests() : base(new RetentionFixture()) { }

    [Fact]
    public async Task SustainedNonemptyInbox_CompactsAtExpiryAndPreservesCurrentRecords()
    {
        var receiver = await StartBacklogAsync();
        var owner = (await receiver.GetSnapshotAsync()).InboxJobId;
        using var old = CreateEnvelope(receiver, NewMessage(120, "old"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, old.Value)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 1);
        _ = await receiver.GetSnapshotAsync();
        Fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        using var current = CreateEnvelope(receiver, NewMessage(121, "current"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, current.Value)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 2);
        _ = await receiver.GetSnapshotAsync();
        Fixture.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, old.Value)).Status);
        var processed = GetProcessed(receiver);
        Assert.Equal(2, processed.Count);

        Fixture.Clock.Advance(TimeSpan.FromTicks(1));
        using var fresh = CreateEnvelope(receiver, NewMessage(122, "fresh"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, fresh.Value)).Status);
        var maintained = await Fixture.WaitForEffectCountAsync(receiver, 3);
        _ = await receiver.GetSnapshotAsync();

        Assert.Equal(1, maintained.InboxCount);
        Assert.Equal(owner, maintained.InboxJobId);
        Assert.Empty(maintained.InboxDeadLetters);
        Assert.Equal(2, processed.Count);
        Assert.False(processed.ContainsKey((old.Value.SenderId, old.Value.MessageId)));
        Assert.True(processed.ContainsKey((current.Value.SenderId, current.Value.MessageId)));
        Assert.True(processed.ContainsKey((fresh.Value.SenderId, fresh.Value.MessageId)));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, current.Value)).Status);
    }

    [Fact]
    public async Task FreshActivation_CompactsProcessedRecordsWithoutDeadLettersOrOwnerClear()
    {
        var receiver = await StartBacklogAsync();
        using var old = CreateEnvelope(receiver, NewMessage(123, "initial-expiry"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, old.Value)).Status);
        var before = await Fixture.WaitForEffectCountAsync(receiver, 1);
        _ = await receiver.GetSnapshotAsync();
        Fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        await receiver.RequestDeactivationAsync();
        var after = await receiver.GetSnapshotAsync();

        Assert.NotEqual(before.ActivationId, after.ActivationId);
        Assert.Equal(1, after.InboxCount);
        Assert.Equal(before.InboxJobId, after.InboxJobId);
        Assert.Empty(after.InboxDeadLetters);
        Assert.Empty(GetProcessed(receiver));
        Assert.Equal(1, Assert.Single(after.Effects).Count);
    }

    [Fact]
    public async Task BusyInbox_AmortizesScansAcrossRetentionCadence()
    {
        var receiver = await StartBacklogAsync();
        var context = Fixture.GetGrainContext(receiver);
        var extension = context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        var counted = new CountingProcessedDictionary(GetProcessed(receiver));
        extension.GetType().GetField("_processed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(extension, counted);
        using var oldest = CreateEnvelope(receiver, NewMessage(124, "oldest"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, oldest.Value)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 1);
        _ = await receiver.GetSnapshotAsync();
        Fixture.Clock.Advance(TimeSpan.FromTicks(1));
        using var next = CreateEnvelope(receiver, NewMessage(125, "next"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, next.Value)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 2);
        _ = await receiver.GetSnapshotAsync();
        Assert.Equal(0, counted.Enumerations);
        Fixture.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
        using var trigger = CreateEnvelope(receiver, NewMessage(126, "sweep"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, trigger.Value)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 3);
        _ = await receiver.GetSnapshotAsync();
        Assert.Equal(1, counted.Enumerations);
        Assert.False(counted.ContainsKey((oldest.Value.SenderId, oldest.Value.MessageId)));
        Assert.True(counted.ContainsKey((next.Value.SenderId, next.Value.MessageId)));
        Fixture.Clock.Advance(TimeSpan.FromTicks(1));
        for (var i = 0; i < 8; i++)
        {
            using var message = CreateEnvelope(receiver, NewMessage(130 + i, "within-cadence"));
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, message.Value)).Status);
            await Fixture.WaitForEffectCountAsync(receiver, 4 + i);
            _ = await receiver.GetSnapshotAsync();
        }
        Assert.Equal(1, counted.Enumerations);
        Assert.True(counted.ContainsKey((next.Value.SenderId, next.Value.MessageId)));
        Fixture.Clock.Advance(TimeSpan.FromMinutes(2.5) - TimeSpan.FromTicks(1));
        using var following = CreateEnvelope(receiver, NewMessage(140, "following-sweep"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, following.Value)).Status);
        var after = await Fixture.WaitForEffectCountAsync(receiver, 12);
        _ = await receiver.GetSnapshotAsync();
        Assert.Equal(2, counted.Enumerations);
        Assert.False(counted.ContainsKey((next.Value.SenderId, next.Value.MessageId)));
        Assert.True(counted.ContainsKey((trigger.Value.SenderId, trigger.Value.MessageId)));
        Assert.Equal(1, after.InboxCount);
    }

    [Fact]
    public async Task PumpMaintenance_WritesOnlyWhenExpiredRecordsExist()
    {
        var receiver = await StartBacklogAsync();
        using var old = CreateEnvelope(receiver, NewMessage(141, "pump-expiry"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, old.Value)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 1);
        var owned = await receiver.GetSnapshotAsync();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        Assert.Equal(DurableJobRunStatus.RescheduleRequested, (await RunPumpAsync(receiver)).Status);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Fixture.Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(DurableJobRunStatus.RescheduleRequested, (await RunPumpAsync(receiver)).Status);

        var maintained = await receiver.GetSnapshotAsync();
        Assert.Equal(writes + 1, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Empty(GetProcessed(receiver));
        Assert.Equal(1, maintained.InboxCount);
        Assert.Equal(owned.InboxJobId, maintained.InboxJobId);
        Assert.Empty(maintained.InboxDeadLetters);
        Assert.Equal(1, Assert.Single(maintained.Effects).Count);
        Assert.Equal(DurableJobRunStatus.RescheduleRequested, (await RunPumpAsync(receiver)).Status);
        Assert.Equal(writes + 1, Fixture.Storage.GetSuccessfulWriteCount(journal));
    }

    [Fact]
    public async Task MaintenanceWriteFailure_FencesOldStateAndFreshReplayRetriesCompaction()
    {
        var receiver = await StartBacklogAsync();
        using var old = CreateEnvelope(receiver, NewMessage(142, "failed-maintenance"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, old.Value)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 1);
        _ = await receiver.GetSnapshotAsync();
        var oldContext = Fixture.GetGrainContext(receiver);
        var oldGrain = Assert.IsType<DurableMessagingTestGrain>(oldContext.GrainInstance);
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        Fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        Fixture.Storage.FailWrite(journal);

        var failure = await Assert.ThrowsAsync<IOException>(() => RunPumpAsync(receiver));

        Assert.Same(failure, await oldGrain.Faulted.Task);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(0, oldGrain.GetSnapshotForTest().ProcessedMessageCount);
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var recovered = await receiver.GetSnapshotAsync();
        var newGrain = Assert.IsType<DurableMessagingTestGrain>(Fixture.GetGrainContext(receiver).GrainInstance);
        Assert.NotEqual(oldGrain.GetSnapshotForTest().ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.IsType<DurableEndpointSnapshot>(newGrain.ReplayedSnapshot).ProcessedMessageCount);
        Assert.Equal(0, recovered.ProcessedMessageCount);
        Assert.Equal(1, recovered.InboxCount);
        Assert.Equal(writes + 1, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
    }

    private async Task<DurableJobRunResult> RunPumpAsync(IDurableMessagingTestGrain receiver)
    {
        var snapshot = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var extension = (IDurableJobFeatureHandler)context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        var run = new PumpContext(Assert.IsType<DurableJob>(snapshot.InboxJob));
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        var stopped = events.WaitForEventAsync(nameof(GrainTimerEvents.TickStop),
            item => item.Payload is GrainTimerEvents.TickStop stop && ReferenceEquals(stop.GrainContext, context),
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        DurableJobRunResult result = null!;
        await OnTurnAsync(context, () => result = extension.ExecuteJobAsync(run, TestContext.Current.CancellationToken).GetAwaiter().GetResult());
        Assert.Equal(DurableJobRunStatus.InProgress, result.Status);
        Assert.Null(Assert.IsType<GrainTimerEvents.TickStop>((await stopped).Payload).Exception);
        await OnTurnAsync(context, () => result = extension.ExecuteJobAsync(run, TestContext.Current.CancellationToken).GetAwaiter().GetResult());
        return result;
    }

    private static Task OnTurnAsync(IGrainContext context, Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try { action(); completed.SetResult(); }
            catch (Exception exception) { completed.SetException(exception); }
        });
        return completed.Task;
    }

    private sealed class PumpContext(DurableJob job) : IJobRunContext
    {
        public DurableJob Job { get; } = job;
        public string RunId { get; } = Guid.NewGuid().ToString("N");
        public int DequeueCount => 1;
    }

    private sealed class CountingProcessedDictionary(IDictionary<(GrainId, Guid), DateTimeOffset> inner) : IDictionary<(GrainId, Guid), DateTimeOffset>
    {
        public int Enumerations { get; private set; }
        public DateTimeOffset this[(GrainId, Guid) key] { get => inner[key]; set => inner[key] = value; }
        public ICollection<(GrainId, Guid)> Keys => inner.Keys;
        public ICollection<DateTimeOffset> Values { get { Enumerations++; return inner.Values; } }
        public int Count => inner.Count;
        public bool IsReadOnly => inner.IsReadOnly;
        public void Add((GrainId, Guid) key, DateTimeOffset value) => inner.Add(key, value);
        public void Add(KeyValuePair<(GrainId, Guid), DateTimeOffset> item) => inner.Add(item);
        public void Clear() => inner.Clear();
        public bool Contains(KeyValuePair<(GrainId, Guid), DateTimeOffset> item) => inner.Contains(item);
        public bool ContainsKey((GrainId, Guid) key) => inner.ContainsKey(key);
        public void CopyTo(KeyValuePair<(GrainId, Guid), DateTimeOffset>[] array, int index) => inner.CopyTo(array, index);
        public bool Remove((GrainId, Guid) key) => inner.Remove(key);
        public bool Remove(KeyValuePair<(GrainId, Guid), DateTimeOffset> item) => inner.Remove(item);
        public bool TryGetValue((GrainId, Guid) key, out DateTimeOffset value) => inner.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<(GrainId, Guid), DateTimeOffset>> GetEnumerator() { Enumerations++; return inner.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private async Task<IDurableMessagingTestGrain> StartBacklogAsync()
    {
        var receiver = NewGrain();
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/retained-backlog");
        using var backlog = CreateEnvelope(receiver, NewMessage(119, "backlog") with { ThrowDuringPreparation = true }, "messages/retained-backlog");
        var rescheduled = Fixture.Metrics.GetCount("orleans-durablejobs-jobs-rescheduled");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, backlog.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        handler.Release();
        await Fixture.Metrics.WaitForCountAsync("orleans-durablejobs-jobs-rescheduled", rescheduled + 1);
        var pending = await receiver.GetSnapshotAsync();
        Assert.Equal(1, pending.InboxCount);
        Assert.Empty(pending.Effects);
        Assert.Empty(pending.InboxDeadLetters);
        return receiver;
    }

    private IDurableDictionary<(GrainId, Guid), DateTimeOffset> GetProcessed(IDurableMessagingTestGrain receiver) =>
        Fixture.GetGrainContext(receiver).ActivationServices.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DateTimeOffset>>(
            "__orleans.durable-messaging.inbox-processed");

    private sealed class RetentionFixture : DurableMessagingClusterFixture
    {
        protected override void ConfigureOptions(DurableInboxOptions options)
        {
            base.ConfigureOptions(options);
            options.MaxProcessingAttempts = 10;
            options.BackpressureRetryDelay = TimeSpan.FromHours(1);
        }
    }
}
