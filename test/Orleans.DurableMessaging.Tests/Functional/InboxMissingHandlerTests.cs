using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.TestingHost.Diagnostics;
using Orleans.Timers;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class InboxMissingHandlerTests() : DurableMessagingBehaviorTestBase(new RetryFixture())
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandlerInvocationFailure_BeforeCompletePreservesOriginalCauseAndRetryState(bool deferred)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var extension = context.ActivationServices.GetRequiredService(CancellationCleanupProbe.ExtensionType);
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var failure = new IOException("Original application invocation failure.");
        var armed = new TaskCompletionSource<ControlledJournalStorageProvider.WriteBarrier>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new InvocationFaultHandler(failure, deferred, () => armed.TrySetResult(Fixture.Storage.BlockWrite(journal)));
        await OnTurnAsync(context, () => grain.HandlerOverride = handler);
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        var stopped = WaitForPumpAsync(events, receiver.GetGrainId());
        using var envelope = CreateEnvelope(receiver, NewMessage(192, "invocation-retry"));
        var now = Fixture.Clock.GetUtcNow();
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        var owner = grain.GetSnapshotForTest().InboxJob;
        var ownershipId = grain.GetSnapshotForTest().InboxJobId;
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        if (deferred)
        {
            Assert.False(armed.Task.IsCompleted);
            var initialEntry = Assert.Single(GetAttemptStates(context).Cast<object>());
            var initialState = initialEntry.GetType().GetProperty("Value")!.GetValue(initialEntry)!;
            Assert.Equal(0, initialState.GetType().GetProperty("AttemptCount")!.GetValue(initialState));
            Assert.Null(initialState.GetType().GetProperty("LastError")!.GetValue(initialState));
            Assert.Null(initialState.GetType().GetProperty("NextAttemptAt")!.GetValue(initialState));
            Assert.Equal(1, grain.GetSnapshotForTest().InboxCount);
            Assert.Empty(grain.GetSnapshotForTest().Effects);
            Assert.Equal(0, grain.GetSnapshotForTest().ProcessedMessageCount);
            Assert.Equal(0, grain.GetSnapshotForTest().OutboxCount);
            handler.Release.TrySetResult();
        }
        using var accounting = await armed.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        await accounting.WaitUntilEnteredAsync();
        var pending = CancellationCleanupProbe.Field<IList>(extension, "_pendingWrites");
        var operation = Assert.Single(pending.Cast<object>(), item => item.GetType().Name == "HandlerWrite");
        Assert.Same(failure, operation.GetType().GetProperty("Error")!.GetValue(operation));
        Assert.Equal(false, operation.GetType().GetProperty("Completed")!.GetValue(operation));
        AssertPendingRetry(context, grain.GetSnapshotForTest(), 1, now + TimeSpan.FromMinutes(1));
        Assert.Same(owner, grain.GetSnapshotForTest().InboxJob);
        Assert.Equal(ownershipId, grain.GetSnapshotForTest().InboxJobId);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        await OnTurnAsync(context, () => grain.HandlerOverride = null);
        accounting.Release();
        await stopped;
        Assert.Equal(writes + 1, Fixture.Storage.GetSuccessfulWriteCount(journal));
        AssertPendingRetry(context, await receiver.GetSnapshotAsync(), 1, now + TimeSpan.FromMinutes(1));
        Assert.False(grain.DeactivationFailure.Task.IsCompleted);
        using var retry = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/record");
        Fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        // The grain-facing retry clock is fake while shard-executor scheduling uses
        // real background time. Request the authoritative callback explicitly so this
        // test does not depend on whether the original background dequeue happened
        // before or after the fake-time advance. RunPumpAsync still runs the real
        // non-interleaving inbox timer and awaits that exact requested run's outcome.
        var retryPump = RunPumpAsync(receiver, Assert.IsType<DurableJob>(owner));
        await retry.WaitUntilEnteredAsync();
        AssertPendingRetry(context, grain.GetSnapshotForTest(), 1, now + TimeSpan.FromMinutes(1));
        retry.Release();
        Assert.Equal(DurableJobRunStatus.Completed, (await retryPump).Status);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Equal(0, completed.OutboxCount);
        Assert.Empty(completed.InboxDeadLetters);
        Assert.Equal(1, ScheduleCount(receiver));
        Assert.Same(context, Fixture.GetGrainContext(receiver));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);
    }

    private sealed class InvocationFaultHandler(Exception failure, bool deferred, Action beforeThrow) : IInboxHandler, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entered.TrySetResult();
            if (deferred) return ThrowAfterBarrierAsync(cancellationToken);
            beforeThrow();
            throw failure;
        }
        private async ValueTask ThrowAfterBarrierAsync(CancellationToken cancellationToken)
        {
            await Release.Task.WaitAsync(cancellationToken);
            beforeThrow();
            throw failure;
        }
        public void Dispose() => Release.TrySetResult();
    }

    [Fact]
    public async Task AcceptedHandlerMissingOnFreshActivation_DeadLettersImmediatelyAndRetainsDedupe()
    {
        var receiver = NewGrain();
        const string route = "activation-only/handler";
        _ = await receiver.GetSnapshotAsync();
        await receiver.ConfigureHandlerAsync(false);
        var originalContext = Fixture.GetGrainContext(receiver);
        var originalGrain = Assert.IsType<DurableMessagingTestGrain>(originalContext.GrainInstance);
        using var envelope = CreateEnvelope(receiver, NewMessage(190, "missing-after-replay"), route);
        Assert.Equal(DeliveryStatus.Accepted, (await receiver.AcceptAndDeactivateAsync(envelope.Value)).Status);
        await originalContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        var accepted = Assert.IsType<DurableEndpointSnapshot>(originalGrain.AcceptedSnapshot);
        Assert.Equal(1, accepted.InboxCount);
        Assert.Empty(accepted.Effects);
        var job = Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));
        var now = Fixture.Clock.GetUtcNow();
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        var stopped = WaitForPumpAsync(events, receiver.GetGrainId());

        _ = await receiver.GetSnapshotAsync();
        await stopped;
        var completed = await receiver.GetSnapshotAsync();

        Assert.NotEqual(accepted.ActivationId, completed.ActivationId);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Empty(completed.Effects);
        Assert.Equal(0, completed.OutboxCount);
        Assert.Equal(0, completed.MaxConcurrentHandlers);
        var deadLetter = Assert.Single(completed.InboxDeadLetters);
        Assert.Equal(envelope.Value.MessageId, deadLetter.MessageId);
        Assert.Equal("No inbox handler is registered.", deadLetter.Reason);
        Assert.Equal(0, deadLetter.AttemptCount);
        Assert.Equal(now, deadLetter.DeadLetteredAt);
        var context = Fixture.GetGrainContext(receiver);
        var retained = Assert.Single(context.ActivationServices.GetRequiredService<IDurableMessagingDiagnostics>().InboxDeadLetters);
        Assert.Equal(envelope.Value.SenderId, retained.Message.SenderId);
        Assert.Empty(GetAttemptStates(context));
        var processed = context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<HierarchicalKey, DateTimeOffset>>("__orleans.durable-messaging.inbox-processed");
        Assert.Equal(now, processed[envelope.Value.MessageId]);
        Assert.Equal(job.Id, completed.InboxJob!.Id);
        Assert.Equal(job.ShardId, completed.InboxJob.ShardId);
        Assert.Equal(accepted.InboxJobId, completed.InboxJobId);
        Assert.Equal(1, ScheduleCount(receiver));
        Assert.False(Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance).DeactivationFailure.Task.IsCompleted);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);

        Assert.Equal(DurableJobRunStatus.Completed, (await RunPumpAsync(receiver, job)).Status);
        var retired = await receiver.GetSnapshotAsync();
        Assert.Null(retired.InboxJobId);
        Assert.Null(retired.InboxJob);
        Assert.Equal(1, ScheduleCount(receiver));
        await receiver.RequestDeactivationAsync();
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(completed.ActivationId, recovered.ActivationId);
        Assert.Equal(deadLetter, Assert.Single(recovered.InboxDeadLetters));
        Assert.Equal(1, recovered.ProcessedMessageCount);
        Assert.Empty(recovered.Effects);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);
        Fixture.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);
        Fixture.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(DeliveryStatus.HandlerNotFound, (await DeliverAsync(receiver, envelope.Value)).Status);
        Assert.Equal(deadLetter, Assert.Single((await receiver.GetSnapshotAsync()).InboxDeadLetters));
        Assert.Equal(1, ScheduleCount(receiver));
    }

    [Fact]
    public Task OrdinaryHandlerFailures_RetryUntilExactConfiguredAttemptLimit() =>
        RetryUntilExactConfiguredAttemptLimitAsync(startBackgroundBeforeDuplicate: false);

    [Fact]
    public Task RetryDuplicateDelivery_QueuedBehindActiveHandler_DoesNotDeadlock() =>
        RetryUntilExactConfiguredAttemptLimitAsync(startBackgroundBeforeDuplicate: true);

    private async Task RetryUntilExactConfiguredAttemptLimitAsync(bool startBackgroundBeforeDuplicate)
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(receiver, NewMessage(191, "retry-limit") with { ThrowDuringPreparation = true });
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        var stopped = WaitForPumpAsync(events, receiver.GetGrainId());
        var now = Fixture.Clock.GetUtcNow();
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await stopped;
        var first = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        AssertPendingRetry(context, first, expectedAttempts: 1, now + TimeSpan.FromMinutes(1));
        var job = Assert.IsType<DurableJob>(first.InboxJob);
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        Assert.Equal(DurableJobRunStatus.RescheduleRequested, (await RunPumpAsync(receiver, job)).Status);
        AssertPendingRetry(context, await receiver.GetSnapshotAsync(), expectedAttempts: 1, now + TimeSpan.FromMinutes(1));
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));

        Task<DurableJobRunResult>? background = null;
        using var tick = startBackgroundBeforeDuplicate
            ? Fixture.Clock.CreateTimer(_ => background = RunPumpAsync(receiver, job), null, TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan)
            : null;
        using (var runningLocalDrain = ArmHandlerBeforeAdvance(receiver.GetGrainId(), TestApplicationProtocol.Read(Fixture.Client.ServiceProvider.GetRequiredService<Orleans.Serialization.Session.SerializerSessionPool>(), envelope.Value).Route, TimeSpan.FromMinutes(1)))
        {
            if (startBackgroundBeforeDuplicate)
            {
                await runningLocalDrain.WaitUntilEnteredAsync();
            }
            var duplicate = DeliverAsync(receiver, envelope.Value);
            await runningLocalDrain.WaitUntilEnteredAsync();
            var retry = startBackgroundBeforeDuplicate
                ? Assert.IsAssignableFrom<Task<DurableJobRunResult>>(background)
                : RunPumpAsync(receiver, job);
            await OnTurnAsync(context, static () => { });
            await AssertUnrelatedTimerDoesNotCompleteAsync(context, events, retry);
            if (startBackgroundBeforeDuplicate)
            {
                Assert.False(duplicate.IsCompleted);
            }
            runningLocalDrain.Release();
            Assert.Equal(DurableJobRunStatus.RescheduleRequested, (await retry).Status);
            Assert.Equal(DeliveryStatus.Duplicate, (await duplicate).Status);
        }
        AssertPendingRetry(context, await receiver.GetSnapshotAsync(), expectedAttempts: 2, now + TimeSpan.FromMinutes(3));
        using var runningRequestedPump = ArmHandlerBeforeAdvance(receiver.GetGrainId(), TestApplicationProtocol.Read(Fixture.Client.ServiceProvider.GetRequiredService<Orleans.Serialization.Session.SerializerSessionPool>(), envelope.Value).Route, TimeSpan.FromMinutes(2));
        var terminal = RunPumpAsync(receiver, job);
        await runningRequestedPump.WaitUntilEnteredAsync();
        await AssertUnrelatedTimerDoesNotCompleteAsync(context, events, terminal);
        runningRequestedPump.Release();
        Assert.Equal(DurableJobRunStatus.Completed, (await terminal).Status);
        var completed = await receiver.GetSnapshotAsync();
        var deadLetter = Assert.Single(completed.InboxDeadLetters);
        Assert.Equal(3, deadLetter.AttemptCount);
        Assert.Contains("Injected handler preparation failure", deadLetter.Reason, StringComparison.Ordinal);
        Assert.Equal(envelope.Value.MessageId, deadLetter.MessageId);
        Assert.Empty(completed.Effects);
        Assert.Equal(0, completed.OutboxCount);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Empty(GetAttemptStates(context));
        Assert.Null(completed.InboxJobId);
        Assert.Equal(1, ScheduleCount(receiver));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);
        Assert.False(Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance).DeactivationFailure.Task.IsCompleted);
    }

    [Fact]
    public async Task RetryBarrier_IsArmedWhenClockMakesBackgroundPumpEligible()
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(receiver, NewMessage(192, "clock-boundary") with { ThrowDuringPreparation = true });
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        var stopped = WaitForPumpAsync(events, receiver.GetGrainId());
        var now = Fixture.Clock.GetUtcNow();
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await stopped;
        var first = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var job = Assert.IsType<DurableJob>(first.InboxJob);
        AssertPendingRetry(context, first, expectedAttempts: 1, now + TimeSpan.FromMinutes(1));
        var writes = Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId()));
        HandlerProbe.Barrier? observedBarrier = null;
        Task<DurableJobRunResult>? background = null;
        using var tick = Fixture.Clock.CreateTimer(_ =>
        {
            Fixture.HandlerProbe.TryGet(receiver.GetGrainId(), TestApplicationProtocol.Read(Fixture.Client.ServiceProvider.GetRequiredService<Orleans.Serialization.Session.SerializerSessionPool>(), envelope.Value).Route, out observedBarrier);
            background = RunPumpAsync(receiver, job);
        }, null, TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);

        using var handler = ArmHandlerBeforeAdvance(receiver.GetGrainId(), TestApplicationProtocol.Read(Fixture.Client.ServiceProvider.GetRequiredService<Orleans.Serialization.Session.SerializerSessionPool>(), envelope.Value).Route, TimeSpan.FromMinutes(1));
        Assert.Same(handler, observedBarrier);
        Assert.NotNull(background);
        await handler.WaitUntilEnteredAsync();
        await OnTurnAsync(context, () => AssertPendingRetry(context, grain.GetSnapshotForTest(), expectedAttempts: 1, now + TimeSpan.FromMinutes(1)));
        Assert.False(background.IsCompleted);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId())));
        await AssertUnrelatedTimerDoesNotCompleteAsync(context, events, background);
        handler.Release();
        Assert.Equal(DurableJobRunStatus.RescheduleRequested, (await background).Status);
        AssertPendingRetry(context, await receiver.GetSnapshotAsync(), expectedAttempts: 2, now + TimeSpan.FromMinutes(3));
        Assert.Equal(writes + 1, Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId())));
        Assert.Equal(1, ScheduleCount(receiver));
        Assert.False(grain.DeactivationFailure.Task.IsCompleted);
    }

    private HandlerProbe.Barrier ArmHandlerBeforeAdvance(GrainId grainId, string route, TimeSpan advance)
    {
        var handler = Fixture.HandlerProbe.Arm(grainId, route);
        try
        {
            Fixture.Clock.Advance(advance);
            return handler;
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    private static async Task AssertUnrelatedTimerDoesNotCompleteAsync(IGrainContext context, DiagnosticEventCollector events, Task pump)
    {
        IGrainTimer unrelated = null!;
        await OnTurnAsync(context, () => unrelated = context.ActivationServices.GetRequiredService<ITimerRegistry>()
            .RegisterGrainTimer(context, static (_, _) => Task.CompletedTask, 0,
                new GrainTimerCreationOptions(TimeSpan.Zero, Timeout.InfiniteTimeSpan) { Interleave = true }));
        using (unrelated)
        {
            await events.WaitForEventAsync(nameof(GrainTimerEvents.TickStop),
                item => item.Payload is GrainTimerEvents.TickStop stop && ReferenceEquals(stop.Timer, unrelated),
                TimeSpan.FromSeconds(30), Cancellation);
            Assert.False(pump.IsCompleted);
        }
    }

    private static void AssertPendingRetry(IGrainContext context, DurableEndpointSnapshot snapshot, int expectedAttempts, DateTimeOffset nextAttempt)
    {
        Assert.Equal(1, snapshot.InboxCount);
        Assert.Equal(0, snapshot.ProcessedMessageCount);
        Assert.Empty(snapshot.InboxDeadLetters);
        Assert.Empty(snapshot.Effects);
        Assert.Equal(0, snapshot.OutboxCount);
        var entry = Assert.Single(GetAttemptStates(context).Cast<object>());
        var state = entry.GetType().GetProperty("Value")!.GetValue(entry)!;
        Assert.Equal(expectedAttempts, state.GetType().GetProperty("AttemptCount")!.GetValue(state));
        Assert.Equal(nextAttempt, state.GetType().GetProperty("NextAttemptAt")!.GetValue(state));
    }
    private static IEnumerable GetAttemptStates(IGrainContext context)
    {
        var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        Assert.True(manager.TryGetStateMachine("__orleans.durable-messaging.inbox-message-state", out var state));
        return Assert.IsAssignableFrom<IEnumerable>(state);
    }
    private int ScheduleCount(IDurableMessagingTestGrain receiver) =>
        Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, receiver.GetGrainId());
    private static async Task<DiagnosticEvent> WaitForPumpAsync(DiagnosticEventCollector events, GrainId grainId)
    {
        var created = await events.WaitForEventAsync(nameof(GrainTimerEvents.Created),
            item => item.Payload is GrainTimerEvents.Created timer && timer.GrainContext.GrainId == grainId && IsInboxTimer(timer.Timer),
            TimeSpan.FromSeconds(30), Cancellation);
        var timer = Assert.IsType<GrainTimerEvents.Created>(created.Payload).Timer;
        return await events.WaitForEventAsync(nameof(GrainTimerEvents.TickStop),
            item => item.Payload is GrainTimerEvents.TickStop stop && ReferenceEquals(stop.Timer, timer),
            TimeSpan.FromSeconds(30), Cancellation);
    }
    private static bool IsInboxTimer(IGrainTimer timer) => timer.GetType().GenericTypeArguments is [var state]
        && state.DeclaringType == ReceiverTestServices.GetImplementationType("DurableInboxExtension");
    private async Task<DurableJobRunResult> RunPumpAsync(IDurableMessagingTestGrain receiver, DurableJob job)
    {
        var context = Fixture.GetGrainContext(receiver);
        var feature = (IDurableJobFeatureHandler)context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        var completion = new PumpCompletion(context, feature, new PumpContext(job));
        using var subscription = GrainTimerEvents.AllEvents.Subscribe(completion);
        await OnTurnAsync(context, completion.Start);
        return await completion.Result.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
    }
    private sealed class PumpCompletion(IGrainContext context, IDurableJobFeatureHandler feature, IJobRunContext run)
        : IObserver<GrainTimerEvents.TimerEvent>
    {
        private IGrainTimer? _timer;
        private bool _capturing;
        private bool _started;
        public TaskCompletionSource<DurableJobRunResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Start()
        {
            _started = true;
            StartRequestedPump(allowCoalescing: true);
        }
        private void StartRequestedPump(bool allowCoalescing)
        {
            Assert.Same(context, ReceiverTestServices.CurrentGrainContext);
            DurableJobRunResult result;
            _capturing = true;
            try
            {
                result = feature.ExecuteJobAsync(run, Cancellation).GetAwaiter().GetResult();
            }
            finally
            {
                _capturing = false;
            }
            if (result.IsInProgress && _timer is null)
            {
                // Bind only this exact requested payload, never an unrelated coalesced turn.
                var state = feature.GetType().GetField("_pumpTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(feature);
                if (state is not null)
                {
                    var pending = state.GetType().BaseType!.GetField("_pending", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(state);
                    if (pending is not null)
                    {
                        var execution = pending.GetType().GetProperty("Execution")!.GetValue(pending)!;
                        var key = execution.GetType().GetProperty("Key")!.GetValue(execution)!;
                        if (Equals(run.RunId, key.GetType().GetProperty("RunId")!.GetValue(key))
                            && Equals(run.Job.Id, key.GetType().GetProperty("JobId")!.GetValue(key)))
                        {
                            _timer = Assert.IsAssignableFrom<IGrainTimer>(state.GetType().BaseType!
                                .GetField("_timer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(state));
                        }
                    }
                }
            }
            if (!result.IsInProgress)
            {
                Result.TrySetResult(result);
            }
            else if (!allowCoalescing)
            {
                Assert.NotNull(_timer);
            }
        }
        public void OnNext(GrainTimerEvents.TimerEvent item)
        {
            if (!ReferenceEquals(item.GrainContext, context) || Result.Task.IsCompleted) return;
            try
            {
                if (item is GrainTimerEvents.Created && _capturing)
                {
                    Assert.Null(_timer);
                    _timer = item.Timer;
                }
                else if (item is GrainTimerEvents.TickStop stop && _started)
                {
                    if (ReferenceEquals(stop.Timer, _timer))
                    {
                        Assert.Null(stop.Exception);
                        Assert.Same(context, ReceiverTestServices.CurrentGrainContext);
                        var result = feature.ExecuteJobAsync(run, Cancellation).GetAwaiter().GetResult();
                        Assert.False(result.IsInProgress);
                        Result.TrySetResult(result);
                    }
                    else if (_timer is null && IsInboxTimer(stop.Timer))
                    {
                        // The coalesced pump released its lease before this event; start the requested run on this turn.
                        Assert.Null(stop.Exception);
                        StartRequestedPump(allowCoalescing: false);
                    }
                }
            }
            catch (Exception exception)
            {
                Result.TrySetException(exception);
            }
        }
        public void OnError(Exception error) => Result.TrySetException(error);
        public void OnCompleted() { }
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
    private sealed class PumpContext(DurableJob job) : IJobRunContext
    {
        public DurableJob Job { get; } = job;
        public string RunId { get; } = Guid.NewGuid().ToString("N");
        public int DequeueCount => 1;
    }
    private sealed class RetryFixture : DurableMessagingClusterFixture
    {
        protected override void ConfigureOptions(DurableInboxOptions options)
        {
            base.ConfigureOptions(options);
            options.MaxProcessingAttempts = 3;
            options.BackpressureRetryDelay = TimeSpan.FromMinutes(1);
        }
    }
}
