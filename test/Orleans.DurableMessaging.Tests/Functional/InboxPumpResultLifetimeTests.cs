using System.Collections;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
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
public sealed class InboxPumpResultLifetimeTests : DurableMessagingBehaviorTestBase
{
    [Fact]
    public async Task ReplacedQueuedPump_DiscardsUnconsumableExecutionAndRegistration()
    {
        var receiver = NewGrain();
        var oldJob = CreateJob(receiver, "replaced:1");
        await receiver.SetInboxOwnershipAsync("replaced:1", oldJob);
        await RefreshSeededOwnerAsync(receiver);
        var context = Fixture.GetGrainContext(receiver);
        var results = GetEntries(context);
        using var hold = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "hold-replacement");
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/replacement");
        using var envelope = CreateEnvelope(receiver, NewMessage(170, "replacement"), "messages/replacement");
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        var turn = receiver.HoldPumpTurnAsync("hold-replacement", envelope.Value, deactivate: false);
        await hold.WaitUntilEnteredAsync();
        var run = CreateRun(oldJob);
        Assert.Equal(DurableJobRunStatus.InProgress, (await InvokeAsync(receiver, run)).Status);
        var entry = Assert.Single(results.Values.Cast<object>());
        Assert.NotEqual(default, GetRegistration(entry));
        var timer = Assert.Single(events.Events.Select(static e => e.Payload).OfType<GrainTimerEvents.Created>(), e => ReferenceEquals(e.GrainContext, context)).Timer;
        var stopped = events.WaitForEventAsync(nameof(GrainTimerEvents.TickStop),
            e => e.Payload is GrainTimerEvents.TickStop stop && ReferenceEquals(stop.Timer, timer),
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        hold.Release();
        await turn;
        await stopped;
        await handler.WaitUntilEnteredAsync();

        Assert.DoesNotContain(results.Keys.Cast<object>(), key => GetJobId(key) == oldJob.Id);
        Assert.Equal(default, GetRegistration(entry));
        Assert.Equal(DurableJobRunStatus.Completed, (await InvokeAsync(receiver, CreateRun(oldJob, 2))).Status);
        Assert.False(Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance).DeactivationFailure.Task.IsCompleted);
        handler.Release();
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
    }

    [Fact]
    public async Task OwnerClearCompletion_PollConsumesResultBeforeOwnershipMismatchReturns()
    {
        var receiver = NewGrain();
        var job = CreateJob(receiver, "clear:1");
        await receiver.SetInboxOwnershipAsync("clear:1", job);
        await RefreshSeededOwnerAsync(receiver);
        var context = Fixture.GetGrainContext(receiver);
        var entries = GetEntries(context);
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        var stopped = events.WaitForEventAsync(nameof(GrainTimerEvents.TickStop),
            e => e.Payload is GrainTimerEvents.TickStop stop && ReferenceEquals(stop.GrainContext, context),
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var run = CreateRun(job);
        Assert.Equal(DurableJobRunStatus.InProgress, (await InvokeAsync(receiver, run)).Status);
        await stopped;
        Assert.Null((await receiver.GetSnapshotAsync()).InboxJobId);
        Assert.Single(entries);
        var feature = (IDurableJobFeatureHandler)context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        DurableJobRunResult result = null!;
        await OnTurnAsync(context, () => result = feature.ExecuteJobAsync(run, TestContext.Current.CancellationToken).GetAwaiter().GetResult());
        Assert.Equal(DurableJobRunStatus.Completed, result.Status);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task ShutdownWithQueuedPump_ReleasesRegistrationAndFreshActivationRetries()
    {
        var receiver = NewGrain();
        var job = CreateJob(receiver, "shutdown:1");
        await receiver.SetInboxOwnershipAsync("shutdown:1", job);
        await RefreshSeededOwnerAsync(receiver);
        using var envelope = CreateEnvelope(receiver, NewMessage(171, "fresh-after-stop"));
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var entries = GetEntries(context);
        var feature = (IDurableJobFeatureHandler)context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        using var hold = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "hold-stop");
        var turn = receiver.HoldPumpTurnAsync("hold-stop", replacement: null, deactivate: true);
        await hold.WaitUntilEnteredAsync();
        var run = CreateRun(job);
        Assert.Equal(DurableJobRunStatus.InProgress, (await InvokeAsync(receiver, run)).Status);
        var entry = Assert.Single(entries.Values.Cast<object>());
        Assert.NotEqual(default, GetRegistration(entry));
        var messages = context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>(
            "__orleans.durable-messaging.inbox");
        var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        Task persisted = null!;
        await OnTurnAsync(context, () =>
        {
            messages.Add((envelope.Value.SenderId, envelope.Value.MessageId), envelope.Value);
            persisted = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        });
        await persisted;
        hold.Release();
        await turn;
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Empty(entries);
        Assert.Equal(default, GetRegistration(entry));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await feature.ExecuteJobAsync(run, TestContext.Current.CancellationToken));
        Assert.Empty(grain.GetSnapshotForTest().Effects);
        _ = await receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.NotEqual(grain.GetSnapshotForTest().ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
    }

    [Fact]
    public async Task TerminalFaultWithQueuedPump_ReleasesEntryWithoutCompletingDurableJob()
    {
        var receiver = NewGrain();
        var job = CreateJob(receiver, "fault:1");
        await receiver.SetInboxOwnershipAsync("fault:1", job);
        await RefreshSeededOwnerAsync(receiver);
        await receiver.StageEffectAsync(new DurableEffect(Guid.NewGuid(), 1, 172, "uncommitted"));
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var entries = GetEntries(context);
        using var hold = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "hold-fault");
        var turn = receiver.HoldPumpTurnAsync("hold-fault", replacement: null, deactivate: false);
        await hold.WaitUntilEnteredAsync();
        var run = CreateRun(job);
        Assert.Equal(DurableJobRunStatus.InProgress, (await InvokeAsync(receiver, run)).Status);
        var entry = Assert.Single(entries.Values.Cast<object>());
        Fixture.Storage.FailWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        var failedWrite = new DurableJob
        {
            Id = Guid.NewGuid().ToString("N"),
            ShardId = "pump-result-lifetime",
            Name = "test/write-journal",
            TargetGrainId = receiver.GetGrainId(),
            DueTime = DateTimeOffset.UtcNow
        };

        var result = await InvokeAsync(receiver, CreateRun(failedWrite));

        Assert.Equal(DurableJobRunStatus.Failed, result.Status);
        Assert.IsType<IOException>(await grain.DeactivationFailure.Task);
        hold.Release();
        await turn;
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Empty(entries);
        Assert.Equal(default, GetRegistration(entry));
        Assert.Equal(job.Id, grain.GetSnapshotForTest().InboxJob!.Id);
        Assert.Single(grain.GetSnapshotForTest().Effects);
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
    }

    [Fact]
    public async Task CanceledQueuedAttempt_DiscardsOnlySupersededRunAndAllowsRetry()
    {
        var receiver = NewGrain();
        var job = CreateJob(receiver, "attempt:1");
        await receiver.SetInboxOwnershipAsync("attempt:1", job);
        await RefreshSeededOwnerAsync(receiver);
        var context = Fixture.GetGrainContext(receiver);
        var entries = GetEntries(context);
        var feature = (IDurableJobFeatureHandler)context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        using var hold = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "hold-attempt");
        var turn = receiver.HoldPumpTurnAsync("hold-attempt", replacement: null, deactivate: false);
        await hold.WaitUntilEnteredAsync();
        using var cancellation = new CancellationTokenSource();
        var canceledRun = CreateRun(job);
        var retryRun = CreateRun(job, 2);
        await OnTurnAsync(context, () => Assert.Equal(DurableJobRunStatus.InProgress,
            feature.ExecuteJobAsync(canceledRun, cancellation.Token).GetAwaiter().GetResult().Status));
        cancellation.Cancel();
        await OnTurnAsync(context, () =>
        {
            Assert.Throws<OperationCanceledException>(() => feature.ExecuteJobAsync(canceledRun, cancellation.Token).GetAwaiter().GetResult());
            Assert.Equal(DurableJobRunStatus.InProgress,
                feature.ExecuteJobAsync(retryRun, TestContext.Current.CancellationToken).GetAwaiter().GetResult().Status);
        });
        Assert.Equal(2, entries.Count);
        hold.Release();
        await turn;
        await Fixture.SnapshotProbe.WaitAsync(receiver.GetGrainId(), static snapshot => snapshot.InboxJobId is null);
        _ = await receiver.GetSnapshotAsync();
        var retained = Assert.Single(entries.Keys.Cast<object>());
        Assert.Equal(retryRun.RunId, retained.GetType().GetProperty("RunId")!.GetValue(retained));
        await OnTurnAsync(context, () => Assert.Equal(DurableJobRunStatus.Completed,
            feature.ExecuteJobAsync(retryRun, TestContext.Current.CancellationToken).GetAwaiter().GetResult().Status));
        Assert.Empty(entries);
    }

    private static IDictionary GetEntries(IGrainContext context)
    {
        var type = ReceiverTestServices.GetImplementationType("DurableMessagingPumpResults");
        return (IDictionary)type.GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(context.ActivationServices.GetRequiredService(type))!;
    }
    private static string GetJobId(object key) => (string)key.GetType().GetProperty("JobId")!.GetValue(key)!;
    private static CancellationTokenRegistration GetRegistration(object entry) =>
        (CancellationTokenRegistration)entry.GetType().GetProperty("CancellationRegistration")!.GetValue(entry)!;
    private static DurableJob CreateJob(IDurableMessagingTestGrain receiver, string owner) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ShardId = "pump-result-lifetime",
        Name = ReceiverTestServices.InboxJobName,
        TargetGrainId = receiver.GetGrainId(),
        DueTime = DateTimeOffset.UtcNow,
        Metadata = new Dictionary<string, string> { ["orleans.messaging.ownership-id"] = owner }
    };
    private static IJobRunContext CreateRun(DurableJob job, int dequeueCount = 1) =>
        (IJobRunContext)Activator.CreateInstance(typeof(DurableJob).Assembly.GetType("Orleans.DurableJobs.JobRunContext", throwOnError: true)!,
            job, Guid.NewGuid().ToString("N"), dequeueCount)!;
    private static async Task<DurableJobRunResult> InvokeAsync(IDurableMessagingTestGrain receiver, IJobRunContext run)
    {
        var type = typeof(DurableJob).Assembly.GetType("Orleans.DurableJobs.IDurableJobReceiverExtension", throwOnError: true)!;
        return await (ValueTask<DurableJobRunResult>)type.GetMethod("HandleDurableJobAsync")!
            .Invoke(receiver.AsReference(type), [run, TestContext.Current.CancellationToken])!;
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
}
