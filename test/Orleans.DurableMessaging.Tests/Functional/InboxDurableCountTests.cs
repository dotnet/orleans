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
public sealed class InboxDurableCountTests() : DurableMessagingBehaviorTestBase(new CountFixture())
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(128)]
    public async Task QueuedDeliveryBurst_CountChecksPerformConstantCollectionWork(int messages)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var probe = new CountProbe(context);
        using var hold = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "hold-count-burst");
        var turn = receiver.HoldPumpTurnAsync("hold-count-burst", replacement: null, deactivate: false);
        await hold.WaitUntilEnteredAsync();
        try
        {
            for (var i = 0; i < messages; i++)
            {
                using var envelope = CreateEnvelope(receiver, NewMessage(i, "queued-count"));
                Assert.Equal(DeliveryStatus.Accepted, (await StartDelivery(context, probe.Extension, envelope.Value, Cancellation)).Status);
            }
            await OnTurnAsync(context, () =>
            {
                Assert.Equal(new Counts(messages, 0, messages), probe.Read());
                Assert.Equal(0, probe.Dictionary.KeyCollections);
                Assert.Equal(0, probe.Dictionary.KeyVisits);
                Assert.Equal(0, probe.Dictionary.EntryEnumerations);
                var reads = probe.Dictionary.CountReads;
                Assert.Equal(messages, probe.DurableCount());
                Assert.Equal(reads + 1, probe.Dictionary.CountReads);
            });
            Assert.Equal(1, ScheduleCount(receiver));
        }
        finally
        {
            hold.Release();
        }
        await turn;
        await Fixture.WaitForEffectCountAsync(receiver, messages);
        _ = await receiver.GetSnapshotAsync();
        await AssertCountsAsync(context, probe, new(0, 0, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task AcceptanceCount_TracksPreparationCommitCancellationAndAcknowledgement(int existing)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var probe = new CountProbe(context);
        using var hold = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "hold-count-phases");
        var turn = receiver.HoldPumpTurnAsync("hold-count-phases", replacement: null, deactivate: false);
        await hold.WaitUntilEnteredAsync();
        try
        {
            for (var i = 0; i < existing; i++)
            {
                using var queued = CreateEnvelope(receiver, NewMessage(i, "existing"));
                Assert.Equal(DeliveryStatus.Accepted, (await StartDelivery(context, probe.Extension, queued.Value, Cancellation)).Status);
            }
            var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
            using var preparation = outbox.BlockNextPreparation();
            var storage = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
            try
            {
                using var scheduling = existing == 0 ? Fixture.JobManagerProbe.BlockNext(ReceiverTestServices.InboxJobName) : null;
                using var envelope = CreateEnvelope(receiver, NewMessage(140, "pending-count"));
                using var cancellation = new CancellationTokenSource();
                var delivery = StartDelivery(context, probe.Extension, envelope.Value, cancellation.Token);
                if (scheduling is not null)
                {
                    await scheduling.WaitUntilEnteredAsync();
                    await AssertCountsAsync(context, probe, new(0, 0, 0));
                    scheduling.Continue();
                }
                await preparation.WaitAsync();
                await AssertCountsAsync(context, probe, new(existing, 0, existing));
                var duplicate = StartDelivery(context, probe.Extension, envelope.Value, Cancellation);
                preparation.Release();
                await storage.WaitUntilEnteredAsync();
                await AssertCountsAsync(context, probe, new(existing + 1, 1, existing));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
                await AssertCountsAsync(context, probe, new(existing + 1, 1, existing));
                Assert.False(duplicate.IsCompleted);
                var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
                await OnTurnAsync(context, () =>
                {
                    var error = Assert.Throws<InvalidOperationException>(() => manager.DeleteStateAsync(Cancellation).GetAwaiter().GetResult());
                    Assert.Contains("quiescent", error.Message, StringComparison.Ordinal);
                });
                var acknowledged = new TaskCompletionSource<Counts>(TaskCreationOptions.RunContinuationsAsynchronously);
                outbox.AfterWriteCompleted = () =>
                {
                    outbox.AfterWriteCompleted = null;
                    acknowledged.TrySetResult(probe.Read());
                };
                storage.Release();
                Assert.Equal(new Counts(existing + 1, 0, existing + 1), await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancellation));
                Assert.Equal(DeliveryStatus.Duplicate, (await duplicate).Status);
                await AssertCountsAsync(context, probe, new(existing + 1, 0, existing + 1));
                Assert.Equal(1, ScheduleCount(receiver));
            }
            finally
            {
                storage.Release();
            }
        }
        finally
        {
            hold.Release();
        }
        await turn;
        await Fixture.WaitForEffectCountAsync(receiver, existing + 1);
        _ = await receiver.GetSnapshotAsync();
        await AssertCountsAsync(context, probe, new(0, 0, 0));
        var job = Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));
        Assert.Equal(DurableJobRunStatus.Completed, (await RunPumpAsync(context, probe.Extension, job)).Status);
        var completed = await receiver.GetSnapshotAsync();
        Assert.Null(completed.InboxJobId);
        Assert.Equal(existing + 1, completed.ProcessedMessageCount);
        await AssertCountsAsync(context, probe, new(0, 0, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAcceptanceCount_PreservesOldStateAndUsesFreshReplay(bool committed)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var probe = new CountProbe(context);
        using var hold = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "hold-count-fault");
        using var handlers = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/count-replay");
        var turn = receiver.HoldPumpTurnAsync("hold-count-fault", replacement: null, deactivate: false);
        await hold.WaitUntilEnteredAsync();
        try
        {
            using var first = CreateEnvelope(receiver, NewMessage(150, "acknowledged"), "messages/count-replay");
            Assert.Equal(DeliveryStatus.Accepted, (await StartDelivery(context, probe.Extension, first.Value, Cancellation)).Status);
            var journal = JournalId.FromGrainId(receiver.GetGrainId());
            var storage = Fixture.Storage.BlockWrite(journal);
            try
            {
                if (committed) Fixture.Storage.FailAfterWrite(journal);
                using var second = CreateEnvelope(receiver, NewMessage(151, "ambiguous"), "messages/count-replay");
                var delivery = StartDelivery(context, probe.Extension, second.Value, Cancellation);
                await storage.WaitUntilEnteredAsync();
                await AssertCountsAsync(context, probe, new(2, 1, 1));
                if (committed) storage.Release();
                else storage.Fail();
                var failure = await Assert.ThrowsAsync<IOException>(() => delivery);
                Assert.Same(failure, await grain.Faulted.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancellation));
                await AssertCountsAsync(context, probe, new(2, 1, 1));
            }
            finally
            {
                storage.Release();
            }
        }
        finally
        {
            hold.Release();
        }
        await turn;
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        Assert.Equal(new Counts(2, 1, 1), probe.Read());
        _ = await receiver.GetSnapshotAsync();
        await handlers.WaitUntilEnteredAsync();
        var freshContext = Fixture.GetGrainContext(receiver);
        Assert.NotSame(context, freshContext);
        var fresh = new CountProbe(freshContext);
        var restored = committed ? 2 : 1;
        await AssertCountsAsync(freshContext, fresh, new(restored, 0, restored));
        Assert.Equal(new Counts(2, 1, 1), probe.Read());
        handlers.Release();
        await Fixture.WaitForEffectCountAsync(receiver, restored);
        _ = await receiver.GetSnapshotAsync();
        await AssertCountsAsync(freshContext, fresh, new(0, 0, 0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectedAcceptanceCount_LeavesCollectionsEmpty(bool scheduling)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var probe = new CountProbe(context);
        if (scheduling) Fixture.JobManagerProbe.FailNext(ReceiverTestServices.InboxJobName);
        else Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance).NextWriteRejection = new InvalidOperationException("Expected count request veto.");
        using var envelope = CreateEnvelope(receiver, NewMessage(152, "rejected"));
        if (scheduling)
        {
            var failure = await Assert.ThrowsAsync<IOException>(() => DeliverAsync(receiver, envelope.Value));
            Assert.Contains("Injected durable job scheduling failure", failure.Message, StringComparison.Ordinal);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => DeliverAsync(receiver, envelope.Value));
            Assert.Equal("Expected count request veto.", failure.Message);
        }
        await AssertCountsAsync(context, probe, new(0, 0, 0));
        Assert.Equal(0, Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId())));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 1);
        _ = await receiver.GetSnapshotAsync();
        await AssertCountsAsync(context, probe, new(0, 0, 0));
    }

    [Fact]
    public async Task QuiescentDeletionCount_ResetsBeforeLaterAcceptanceAndRecovery()
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(receiver, NewMessage(153, "delete-pending"));
        var job = new DurableJob
        {
            Id = "count-delete-job",
            ShardId = "count-delete-shard",
            Name = ReceiverTestServices.InboxJobName,
            TargetGrainId = receiver.GetGrainId(),
            DueTime = Fixture.Clock.GetUtcNow(),
            Metadata = new Dictionary<string, string> { ["orleans.messaging.ownership-id"] = "count-delete:1" }
        };
        await receiver.SeedInboxStateAsync(envelope.Value, "count-delete:1", job);
        var context = Fixture.GetGrainContext(receiver);
        var probe = new CountProbe(context);
        await AssertCountsAsync(context, probe, new(1, 0, 1));
        var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        Task deletion = null!;
        await OnTurnAsync(context, () => deletion = manager.DeleteStateAsync(Cancellation).AsTask());
        await deletion;
        await AssertCountsAsync(context, probe, new(0, 0, 0));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 1);
        _ = await receiver.GetSnapshotAsync();
        await AssertCountsAsync(context, probe, new(0, 0, 0));
        await receiver.RequestDeactivationAsync();
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        _ = await receiver.GetSnapshotAsync();
        var freshContext = Fixture.GetGrainContext(receiver);
        Assert.NotSame(context, freshContext);
        await AssertCountsAsync(freshContext, new CountProbe(freshContext), new(0, 0, 0));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);
    }

    private int ScheduleCount(IDurableMessagingTestGrain receiver) =>
        Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, receiver.GetGrainId());
    private static Task AssertCountsAsync(IGrainContext context, CountProbe probe, Counts expected) =>
        OnTurnAsync(context, () => Assert.Equal(expected, probe.Read()));
    private static Task<DeliveryResult> StartDelivery(IGrainContext context, IDurableInboxExtension extension, DurableEnvelope envelope, CancellationToken cancellation)
    {
        var started = new TaskCompletionSource<Task<DeliveryResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try { started.SetResult(extension.DeliverAsync(envelope, cancellation).AsTask()); }
            catch (Exception exception) { started.SetException(exception); }
        });
        return started.Task.Unwrap();
    }
    private static Task OnTurnAsync(IGrainContext context, Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception exception) { done.SetException(exception); }
        });
        return done.Task;
    }
    private static async Task<DurableJobRunResult> RunPumpAsync(IGrainContext context, IDurableInboxExtension extension, DurableJob job)
    {
        var feature = (IDurableJobFeatureHandler)extension;
        var run = new PumpContext(job);
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        DurableJobRunResult result = null!;
        await OnTurnAsync(context, () => result = feature.ExecuteJobAsync(run, Cancellation).GetAwaiter().GetResult());
        if (result.IsInProgress)
        {
            await events.WaitForEventAsync(nameof(GrainTimerEvents.TickStop),
                item => item.Payload is GrainTimerEvents.TickStop stop && ReferenceEquals(stop.GrainContext, context),
                TimeSpan.FromSeconds(30), Cancellation);
            await OnTurnAsync(context, () => result = feature.ExecuteJobAsync(run, Cancellation).GetAwaiter().GetResult());
        }
        return result;
    }
    private sealed record Counts(int Stored, int Provisional, int Durable, bool ProvisionalSubset = true);
    private sealed class CountProbe
    {
        private readonly HashSet<(GrainId, Guid)> _provisional;
        public CountProbe(IGrainContext context)
        {
            var type = ReceiverTestServices.GetImplementationType("DurableInboxExtension");
            Extension = (IDurableInboxExtension)context.ActivationServices.GetRequiredService(type);
            var field = type.GetField("_inboxDict", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Dictionary = new CountingDictionary((IDictionary<(GrainId, Guid), DurableEnvelope>)field.GetValue(Extension)!);
            field.SetValue(Extension, Dictionary);
            _provisional = (HashSet<(GrainId, Guid)>)type.GetField("_provisionalAcceptances", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Extension)!;
            DurableCount = type.GetMethod("GetDurableInboxCount", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<int>>(Extension);
        }
        public IDurableInboxExtension Extension { get; }
        public CountingDictionary Dictionary { get; }
        public Func<int> DurableCount { get; }
        public Counts Read() => new(Dictionary.Inner.Count, _provisional.Count, DurableCount(), _provisional.All(Dictionary.Inner.ContainsKey));
    }
    private sealed class CountingDictionary(IDictionary<(GrainId, Guid), DurableEnvelope> inner) : IDictionary<(GrainId, Guid), DurableEnvelope>
    {
        public IDictionary<(GrainId, Guid), DurableEnvelope> Inner => inner;
        public int CountReads { get; private set; }
        public int KeyCollections { get; private set; }
        public int KeyVisits { get; private set; }
        public int EntryEnumerations { get; private set; }
        public int Count { get { CountReads++; return inner.Count; } }
        public ICollection<(GrainId, Guid)> Keys { get { KeyCollections++; return new CountingKeys(this); } }
        public ICollection<DurableEnvelope> Values => inner.Values;
        public DurableEnvelope this[(GrainId, Guid) key] { get => inner[key]; set => inner[key] = value; }
        public bool IsReadOnly => inner.IsReadOnly;
        public void Add((GrainId, Guid) key, DurableEnvelope value) => inner.Add(key, value);
        public void Add(KeyValuePair<(GrainId, Guid), DurableEnvelope> item) => inner.Add(item);
        public void Clear() => inner.Clear();
        public bool Contains(KeyValuePair<(GrainId, Guid), DurableEnvelope> item) => inner.Contains(item);
        public bool ContainsKey((GrainId, Guid) key) => inner.ContainsKey(key);
        public void CopyTo(KeyValuePair<(GrainId, Guid), DurableEnvelope>[] array, int index) => inner.CopyTo(array, index);
        public bool Remove((GrainId, Guid) key) => inner.Remove(key);
        public bool Remove(KeyValuePair<(GrainId, Guid), DurableEnvelope> item) => inner.Remove(item);
        public bool TryGetValue((GrainId, Guid) key, out DurableEnvelope value) => inner.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<(GrainId, Guid), DurableEnvelope>> GetEnumerator() { EntryEnumerations++; return inner.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        private sealed class CountingKeys(CountingDictionary owner) : ICollection<(GrainId, Guid)>
        {
            public int Count => owner.Inner.Keys.Count;
            public bool IsReadOnly => true;
            public bool Contains((GrainId, Guid) item) => owner.Inner.Keys.Contains(item);
            public void CopyTo((GrainId, Guid)[] array, int index) => owner.Inner.Keys.CopyTo(array, index);
            public IEnumerator<(GrainId, Guid)> GetEnumerator()
            {
                foreach (var key in owner.Inner.Keys) { owner.KeyVisits++; yield return key; }
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public void Add((GrainId, Guid) item) => throw new NotSupportedException();
            public void Clear() => throw new NotSupportedException();
            public bool Remove((GrainId, Guid) item) => throw new NotSupportedException();
        }
    }
    private sealed class PumpContext(DurableJob job) : IJobRunContext
    {
        public DurableJob Job { get; } = job;
        public string RunId { get; } = Guid.NewGuid().ToString("N");
        public int DequeueCount => 1;
    }
    private sealed class CountFixture : DurableMessagingClusterFixture
    {
        protected override void ConfigureOptions(DurableInboxOptions options)
        {
            base.ConfigureOptions(options);
            options.MaxCapacity = 256;
        }
    }
}
