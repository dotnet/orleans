using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Serialization.Session;
using Orleans.TestingHost.Diagnostics;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class InboxPreparedBatchLifecycleTests : DurableMessagingBehaviorTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_StagesBusinessOutputAndDedupeBeforeHandlerReturn(bool prepared)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Body = async (self, token) =>
        {
            var batch = prepared ? await self.Context.Outbox.PrepareSendAsync([self.Output], token) : null;
            self.Mutate();
            if (batch is null) self.Context.Send(self.Output);
            else self.Context.Send(batch);
            self.Context.Complete();
            AssertCompletedState(rig, self.Context.Envelope);
            Assert.Equal(1, Assert.Single(rig.Effects).Value.Count);
            Assert.Single(rig.Outbox);
        };
        using var input = await DeliverAsync(rig);
        using var storage = Fixture.Storage.BlockAcknowledgement(rig.Journal);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await storage.WaitUntilEnteredAsync();
        Assert.False(finished.IsCompleted);
        if (prepared) Assert.Equal(0, Assert.Single(rig.Outbox.PreparedBatches).DisposeCalls);
        storage.Release();
        await WaitAsync(finished);
        AssertSuccess(rig, input.Value, outputCount: 1);
        if (prepared) AssertDisposed(rig.Outbox, 1);
        await DeactivateAsync(rig);
        var recovered = await rig.Receiver.GetSnapshotAsync();
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(1, recovered.ProcessedMessageCount);
        Assert.Equal(1, recovered.OutboxCount);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, input.Value)).Status);
    }

    [Fact]
    public async Task Complete_PrecedingWriterCapturesCompletionWithBusinessAcrossReturnContinuation()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        using var input = await DeliverAsync(rig);
        using var storage = Fixture.Storage.BlockWrite(rig.Journal);
        var preceding = OnTurnAsync(rig.Context, async () =>
        {
            rig.Context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "prior";
            await rig.Manager.WriteStateAsync(CancellationToken.None);
        });
        await storage.WaitUntilEnteredAsync();
        var queued = OnTurnAsync(rig.Context, async () => await rig.Manager.WriteStateAsync(CancellationToken.None));
        handler.Body = (self, _) =>
        {
            self.Mutate();
            self.Context.Send(self.Output);
            self.Context.Complete();
            AssertCompletedState(rig, input.Value);
            storage.Release();
            return ValueTask.CompletedTask;
        };
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await WaitAsync(Task.WhenAll(preceding, queued, finished));
        Assert.All(rig.Grain.Captures.Where(snapshot => snapshot.Effects.Count != 0), snapshot =>
        {
            Assert.Equal(0, snapshot.InboxCount);
            Assert.Equal(1, snapshot.ProcessedMessageCount);
            Assert.Equal(1, snapshot.OutboxCount);
        });
        AssertSuccess(rig, input.Value, outputCount: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandlerFailure_BeforeVsAfterCompleteUsesActualLogicalOutcome(bool completed)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        var error = new IOException("Handler outcome.");
        handler.Body = (self, _) =>
        {
            if (completed)
            {
                self.Mutate();
                self.Context.Send(self.Output);
                self.Context.Complete();
            }
            throw error;
        };
        using var input = await DeliverAsync(rig);
        var timer = GetTimer(events, rig);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await WaitAsync(finished);
        var stop = (GrainTimerEvents.TickStop)(await TimerStoppedAsync(events, timer)).Payload!;
        if (completed)
        {
            Assert.Same(error, stop.Exception);
            AssertSuccess(rig, input.Value, outputCount: 1);
            Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, input.Value)).Status);
        }
        else
        {
            Assert.Null(stop.Exception);
            Assert.Empty(rig.Effects);
            Assert.Empty(rig.Outbox);
            Assert.Equal(input.Value.MessageId, Assert.Single(rig.Grain.GetSnapshotForTest().InboxDeadLetters).MessageId);
        }
        await AssertHealthyAsync(rig);
        await DeactivateAsync(rig);
        var replay = await rig.Receiver.GetSnapshotAsync();
        Assert.Equal(completed ? 1 : 0, replay.Effects.Count);
        Assert.Equal(1, replay.ProcessedMessageCount);
        Assert.Equal(completed ? 0 : 1, replay.InboxDeadLetters.Count);
    }

    [Fact]
    public async Task SuccessfulReturnWithoutComplete_IsExplicitTerminalMisuse()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Body = (_, _) => ValueTask.CompletedTask;
        using var input = await DeliverAsync(rig);
        var writes = Writes(rig);
        handler.Release.TrySetResult();
        var error = Assert.IsType<InvalidOperationException>(await WaitAsync(rig.Grain.DeactivationFailure.Task));
        Assert.Contains("must call Complete", error.Message, StringComparison.Ordinal);
        Assert.Equal(writes, Writes(rig));
        Assert.Single(rig.Inbox);
        Assert.Empty(rig.Processed);
        Assert.Empty(rig.Effects);
        await AssertFailureReplayAsync(rig, input.Value);
    }

    [Fact]
    public async Task RepeatedComplete_SameActiveAttemptStagesExactlyOnce()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Body = (self, _) =>
        {
            self.Mutate();
            self.Context.Complete();
            var timestamp = Assert.Single(rig.Processed).Value;
            self.Context.Complete();
            Assert.Equal(timestamp, Assert.Single(rig.Processed).Value);
            return ValueTask.CompletedTask;
        };
        using var input = await DeliverAsync(rig);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await WaitAsync(finished);
        AssertSuccess(rig, input.Value, outputCount: 0);
        await AssertHealthyAsync(rig);
    }

    [Theory]
    [InlineData("send")]
    [InlineData("prepare")]
    [InlineData("complete")]
    public async Task RetainedContext_AfterRetirementRejectsAndKeepsOwnerHealthy(string operation)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        using var input = await DeliverAsync(rig);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await WaitAsync(finished);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OnTurnAsync(rig.Context, () => InvokeContextOperation(handler, operation)));
        Assert.Contains("inactive or different attempt", error.Message, StringComparison.Ordinal);
        AssertSuccess(rig, input.Value, outputCount: 0);
        await AssertHealthyAsync(rig);
    }

    [Theory]
    [InlineData("send")]
    [InlineData("prepare")]
    [InlineData("complete")]
    public async Task RetainedContext_DuringOtherAttemptRetainsFirstMisuse(string operation)
    {
        var rig = await CreateAsync();
        using var first = rig.Handler;
        using var one = await DeliverAsync(rig);
        var finished = await FinishedAsync(rig);
        first.Release.TrySetResult();
        await WaitAsync(finished);
        using var second = new TestHandler(rig.Effects);
        InvalidOperationException? rejection = null;
        second.Body = (_, _) =>
        {
            try { InvokeContextOperation(first, operation); }
            catch (InvalidOperationException error) { rejection = error; }
            throw new IOException("Replacement failure.");
        };
        await OnTurnAsync(rig.Context, () =>
            rig.Context.ActivationServices.GetRequiredService<IDurableInbox>().RegisterHandler("async/next", second));
        using var two = CreateEnvelope(rig.Receiver, NewMessage(502, "next"), "async/next");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, two.Value)).Status);
        await WaitAsync(second.Entered.Task);
        second.Release.TrySetResult();
        var failure = await WaitAsync(rig.Grain.DeactivationFailure.Task);
        Assert.Same(Assert.IsType<InvalidOperationException>(rejection), failure);
        Assert.Equal(1, Assert.Single(rig.Effects).Value.Count);
        await WaitAsync(rig.Context.Deactivated);
    }

    [Theory]
    [InlineData("send")]
    [InlineData("prepare")]
    public async Task OperationAfterComplete_RejectsButPersistsCompletedLogicalOutcome(string operation)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        InvalidOperationException? rejection = null;
        handler.Body = (self, _) =>
        {
            self.Mutate();
            self.Context.Complete();
            try { InvokeContextOperation(self, operation); }
            catch (InvalidOperationException error) { rejection = error; }
            return ValueTask.CompletedTask;
        };
        using var input = await DeliverAsync(rig);
        var timer = GetTimer(events, rig);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await WaitAsync(finished);
        Assert.Same(rejection, ((GrainTimerEvents.TickStop)(await TimerStoppedAsync(events, timer)).Payload!).Exception);
        Assert.Contains("already called Complete", rejection!.Message, StringComparison.Ordinal);
        AssertSuccess(rig, input.Value, outputCount: 0);
        Assert.Empty(rig.Outbox.PreparedBatches);
        await AssertHealthyAsync(rig);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnderlyingSendFailure_RetainsFirstCauseEvenWhenCaught(bool prepared, bool replaced)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var error = new IOException("Underlying Send failed.");
        rig.Outbox.NextSendFailure = error;
        handler.Body = async (self, token) =>
        {
            var batch = prepared ? await self.Context.Outbox.PrepareSendAsync([self.Output], token) : null;
            try
            {
                if (batch is null) self.Context.Send(self.Output);
                else self.Context.Outbox.Send(batch);
            }
            catch (IOException)
            {
                if (replaced) throw new InvalidOperationException("Replacement.");
            }
            self.Context.Complete();
        };
        using var input = await DeliverAsync(rig);
        var writes = Writes(rig);
        handler.Release.TrySetResult();
        Assert.Same(error, await WaitAsync(rig.Grain.DeactivationFailure.Task));
        Assert.Equal(writes, Writes(rig));
        Assert.Empty(rig.Effects);
        Assert.Empty(rig.Outbox);
        Assert.Equal(1, rig.Outbox.SendCalls);
        await AssertFailureReplayAsync(rig, input.Value);
        AssertDisposed(rig.Outbox, prepared ? 1 : 0);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("disposed")]
    [InlineData("wrong-attempt")]
    public async Task InvalidBatch_RejectsAndRespectsOriginalOwner(string kind)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        IPreparedOutboxBatch? batch = null;
        if (kind == "foreign")
        {
            await OnTurnAsync(rig.Context, async () =>
                batch = await rig.Outbox.PrepareSendAsync([CreateOutput(rig)], CancellationToken.None));
        }
        else if (kind == "wrong-attempt")
        {
            handler.Body = async (self, token) =>
            {
                batch = await self.Context.Outbox.PrepareSendAsync([self.Output], token);
                self.Context.Complete();
            };
            using var seed = await DeliverAsync(rig);
            var done = await FinishedAsync(rig);
            handler.Release.TrySetResult();
            await WaitAsync(done);
        }
        using var current = kind == "wrong-attempt" ? new TestHandler(rig.Effects) : null;
        var active = current ?? handler;
        if (current is not null)
            await OnTurnAsync(rig.Context, () => rig.Context.ActivationServices.GetRequiredService<IDurableInbox>()
                .RegisterHandler("async/next", current));
        active.Body = async (self, token) =>
        {
            if (kind == "disposed")
            {
                batch = await self.Context.Outbox.PrepareSendAsync([self.Output], token);
                batch.Dispose();
            }
            self.Context.Send(batch!);
            self.Context.Complete();
        };
        using var input = CreateEnvelope(rig.Receiver, NewMessage(503, kind),
            current is null ? "async/handler" : "async/next");
        try
        {
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, input.Value)).Status);
            await WaitAsync(active.Entered.Task);
            active.Release.TrySetResult();
            var error = await WaitAsync(rig.Grain.DeactivationFailure.Task);
            if (kind == "disposed") Assert.IsType<ObjectDisposedException>(error);
            else Assert.IsType<InvalidOperationException>(error);
            await WaitAsync(rig.Context.Deactivated);
            if (kind == "foreign") Assert.Equal(0, Assert.Single(rig.Outbox.PreparedBatches).DisposeCalls);
            else AssertDisposed(rig.Outbox, 1);
        }
        finally
        {
            if (kind == "foreign") batch?.Dispose();
        }
        AssertDisposed(rig.Outbox, 1);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task CaughtAcquisitionFailure_AllowsSafeAlternative(bool synchronous, bool canceled, bool asTask)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        Exception providerFailure = canceled
            ? new OperationCanceledException("Provider canceled.", new CancellationToken(canceled: true))
            : new IOException("Provider failed.");
        Exception? caught = null;
        handler.Body = async (self, token) =>
        {
            try
            {
                var pending = self.Context.Outbox.PrepareSendAsync([self.Output], token);
                if (asTask) await pending.AsTask();
                else await pending;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { caught = error; }
            Assert.Same(rig.Context, ReceiverTestServices.CurrentGrainContext);
            var alternative = await self.Context.Outbox.PrepareSendAsync([self.UnusedOutput], token);
            self.Mutate();
            self.Context.Send(alternative);
            self.Context.Complete();
        };
        using var input = await DeliverAsync(rig);
        using var acquisition = rig.Outbox.BlockNextPreparation();
        if (synchronous) acquisition.Fail(providerFailure);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await acquisition.WaitAsync();
        if (!synchronous) acquisition.Fail(providerFailure);
        await WaitAsync(finished);
        if (canceled)
            Assert.Equal(((OperationCanceledException)providerFailure).CancellationToken,
                Assert.IsAssignableFrom<OperationCanceledException>(caught).CancellationToken);
        else Assert.Same(providerFailure, caught);
        AssertSuccess(rig, input.Value, outputCount: 1);
        AssertDisposed(rig.Outbox, 1);
        await AssertHealthyAsync(rig);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnusedAcquisition_RetiresActualLateOutcomeAfterAck(bool late, bool providerFails)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Body = (self, token) =>
        {
            _ = self.Context.Outbox.PrepareSendAsync([self.Output], token);
            self.Mutate();
            self.Context.Complete();
            return ValueTask.CompletedTask;
        };
        using var input = await DeliverAsync(rig);
        using var acquisition = rig.Outbox.BlockNextPreparation(ignoreCancellation: true);
        var error = new IOException("Unused acquisition failed.");
        if (!late)
        {
            if (providerFails) acquisition.Fail(error);
            else acquisition.Release();
        }
        var finished = await FinishedAsync(rig);
        using var storage = Fixture.Storage.BlockAcknowledgement(rig.Journal);
        handler.Release.TrySetResult();
        await acquisition.WaitAsync();
        await storage.WaitUntilEnteredAsync();
        Assert.False(finished.IsCompleted);
        storage.Release();
        if (late)
        {
            Assert.False(finished.IsCompleted);
            if (providerFails) acquisition.Fail(error);
            else acquisition.Release();
        }
        await WaitAsync(finished);
        AssertSuccess(rig, input.Value, outputCount: 0);
        Assert.True(acquisition.Operation!.Completed.IsCompleted);
        if (providerFails) Assert.Same(error, acquisition.Operation.Failure);
        AssertDisposed(rig.Outbox, providerFails ? 0 : 1);
        await AssertHealthyAsync(rig);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingComplete_DrainsAcquisitionWithoutReplacingMisuse(bool providerFails)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Body = (self, token) =>
        {
            _ = self.Context.Outbox.PrepareSendAsync([self.Output], token);
            return ValueTask.CompletedTask;
        };
        using var input = await DeliverAsync(rig);
        using var acquisition = rig.Outbox.BlockNextPreparation(ignoreCancellation: true);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await acquisition.WaitAsync();
        var error = Assert.IsType<InvalidOperationException>(await WaitAsync(rig.Grain.DeactivationFailure.Task));
        Assert.Contains("must call Complete", error.Message, StringComparison.Ordinal);
        Assert.False(finished.IsCompleted);
        Assert.False(rig.Context.Deactivated.IsCompleted);
        var providerError = new IOException("Late provider.");
        if (providerFails) acquisition.Fail(providerError);
        else acquisition.Release();
        await WaitAsync(finished);
        Assert.Same(error, await rig.Grain.DeactivationFailure.Task);
        await AssertFailureReplayAsync(rig, input.Value);
        AssertDisposed(rig.Outbox, providerFails ? 0 : 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttemptCancellation_BeforeCompleteDrainsLateAcquisitionAndKeepsOwner(bool providerFails)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Body = async (self, token) =>
        {
            using var registration = token.Register(() => canceled.TrySetResult());
            _ = self.Context.Outbox.PrepareSendAsync([self.Output], token);
            await canceled.Task;
            token.ThrowIfCancellationRequested();
        };
        using var input = await DeliverAsync(rig);
        using var acquisition = rig.Outbox.BlockNextPreparation(ignoreCancellation: true);
        var timer = GetTimer(events, rig);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await acquisition.WaitAsync();
        await OnTurnAsync(rig.Context, timer.Dispose);
        await WaitAsync(canceled.Task);
        Assert.False(finished.IsCompleted);
        Assert.False(rig.Grain.DeactivationFailure.Task.IsCompleted);
        var providerError = new IOException("Late canceled acquisition.");
        if (providerFails) acquisition.Fail(providerError);
        else acquisition.Release();
        await WaitAsync(finished);
        await TimerStoppedAsync(events, timer);
        Assert.Single(rig.Inbox);
        Assert.Empty(rig.Processed);
        Assert.Empty(rig.Effects);
        AssertDisposed(rig.Outbox, providerFails ? 0 : 1);
        await AssertHealthyAsync(rig);
        handler.Body = static (self, _) => { self.Mutate(); self.Context.Complete(); return default; };
        Assert.Same(DurableJobRunResult.Completed, await RunPumpAsync(rig));
        AssertSuccess(rig, input.Value, outputCount: 0);
        Assert.Same(rig.Context, Fixture.GetGrainContext(rig.Receiver));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableJobCancellation_BeforeCompleteRetainsExactOwnerAndRetries(bool cancellationCheck)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        using var localEvents = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        using var input = await DeliverAsync(rig);
        var localTimer = GetTimer(localEvents, rig);
        await OnTurnAsync(rig.Context, localTimer.Dispose);
        await TimerStoppedAsync(localEvents, localTimer);
        Assert.False(rig.Grain.DeactivationFailure.Task.IsCompleted);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OperationCanceledException? expected = null;
        handler.Body = async (_, token) =>
        {
            entered.TrySetResult();
            try
            {
                if (cancellationCheck)
                {
                    await resume.Task;
                    token.ThrowIfCancellationRequested();
                }
                else await resume.Task.WaitAsync(token);
            }
            catch (OperationCanceledException error) { expected = error; throw; }
        };
        handler.Release.TrySetResult();
        var snapshot = rig.Grain.GetSnapshotForTest();
        var job = Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(ReceiverTestServices.InboxJobName, rig.Receiver.GetGrainId()));
        var run = new JobContext(job);
        var feature = (IDurableJobFeatureHandler)rig.Context.ActivationServices.GetRequiredService(CancellationCleanupProbe.ExtensionType);
        using var jobEvents = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        using var cancellation = new CancellationTokenSource();
        await OnTurnAsync(rig.Context, async () => Assert.True(
            (await feature.ExecuteJobAsync(run, cancellation.Token)).IsInProgress));
        var timer = GetTimer(jobEvents, rig);
        try
        {
            await WaitAsync(entered.Task);
            await OnTurnAsync(rig.Context, cancellation.Cancel);
        }
        finally
        {
            resume.TrySetResult();
        }
        await TimerStoppedAsync(jobEvents, timer);
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OnTurnAsync(rig.Context, async () => await feature.ExecuteJobAsync(run, TestContext.Current.CancellationToken)));
        Assert.Same(expected, failure);
        Assert.Single(rig.Inbox);
        Assert.Empty(rig.Processed);
        Assert.Empty(rig.Effects);
        Assert.Equal(snapshot.InboxJobId, rig.Grain.GetSnapshotForTest().InboxJobId);
        Assert.Same(snapshot.InboxJob, rig.Grain.GetSnapshotForTest().InboxJob);
        await AssertHealthyAsync(rig);
        handler.Body = static (self, _) => { self.Mutate(); self.Context.Complete(); return default; };
        Assert.Same(DurableJobRunResult.Completed, await RunPumpAsync(rig));
        AssertSuccess(rig, input.Value, outputCount: 0);
        Assert.Same(rig.Context, Fixture.GetGrainContext(rig.Receiver));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationInFinalBlock_CompleteAndOwnedWritePreserveOutcome(bool failWrite)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        IGrainTimer timer = null!;
        handler.Body = async (self, token) =>
        {
            var batch = await self.Context.Outbox.PrepareSendAsync([self.Output], token);
            self.Mutate();
            timer.Dispose();
            self.Context.Send(batch);
            self.Context.Complete();
        };
        using var input = await DeliverAsync(rig);
        timer = GetTimer(events, rig);
        using var storage = Fixture.Storage.BlockWrite(rig.Journal);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await storage.WaitUntilEnteredAsync();
        AssertCompletedState(rig, input.Value);
        Assert.False(finished.IsCompleted);
        Assert.Equal(0, Assert.Single(rig.Outbox.PreparedBatches).DisposeCalls);
        var failure = new OperationCanceledException("Actual storage canceled.", new CancellationToken(canceled: true));
        if (failWrite) storage.Fail(failure);
        else storage.Release();
        await WaitAsync(finished);
        AssertDisposed(rig.Outbox, 1);
        if (failWrite)
        {
            Assert.Same(failure, await WaitAsync(rig.Grain.DeactivationFailure.Task));
            await AssertFailureReplayAsync(rig, input.Value);
        }
        else
        {
            AssertSuccess(rig, input.Value, outputCount: 1);
            await AssertHealthyAsync(rig);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionWrite_HookOutcomePreservesActualCommit(bool postCommit)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        using var input = await DeliverAsync(rig);
        var error = new IOException("Journal hook failed.");
        await OnTurnAsync(rig.Context, () => rig.Manager.Hooks.Add(new JournaledStateHook
        {
            BeforeOperation = postCommit ? null : (_, _) => throw error,
            AfterOperation = postCommit ? (_, _) => throw error : null
        }));
        var timer = GetTimer(events, rig);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await WaitAsync(finished);
        if (postCommit)
        {
            Assert.IsType<JournaledStatePostCommitException>(
                ((GrainTimerEvents.TickStop)(await TimerStoppedAsync(events, timer)).Payload!).Exception);
            AssertSuccess(rig, input.Value, outputCount: 0);
            await OnTurnAsync(rig.Context, rig.Manager.Hooks.Clear);
            await AssertHealthyAsync(rig);
        }
        else
        {
            var failure = Assert.IsType<JournaledStatePreCommitException>(await WaitAsync(rig.Grain.DeactivationFailure.Task));
            Assert.Same(error, failure.InnerException);
            await AssertFailureReplayAsync(rig, input.Value);
        }
    }

    [Fact]
    public async Task PostCompleteHandlerFailure_WithStorageFailureKeepsStorageCauseAuthoritative()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var handlerError = new InvalidOperationException("After Complete.");
        handler.Body = (self, _) => { self.Mutate(); self.Context.Complete(); throw handlerError; };
        using var input = await DeliverAsync(rig);
        using var storage = Fixture.Storage.BlockWrite(rig.Journal);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await storage.WaitUntilEnteredAsync();
        var storageError = new IOException("Storage failed.");
        storage.Fail(storageError);
        await WaitAsync(finished);
        Assert.Same(storageError, await WaitAsync(rig.Grain.DeactivationFailure.Task));
        await AssertFailureReplayAsync(rig, input.Value);
    }

    [Fact]
    public async Task StopAndDelete_DrainCompletedWriteAndLateUnusedAcquisition()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Body = (self, token) =>
        {
            _ = self.Context.Outbox.PrepareSendAsync([self.Output], token);
            self.Mutate();
            self.Context.Complete();
            return default;
        };
        using var input = await DeliverAsync(rig);
        using var acquisition = rig.Outbox.BlockNextPreparation(ignoreCancellation: true);
        using var storage = Fixture.Storage.BlockAcknowledgement(rig.Journal);
        handler.Release.TrySetResult();
        await acquisition.WaitAsync();
        await storage.WaitUntilEnteredAsync();
        var deleting = rig.Receiver.DeleteStateAndDeactivateAsync();
        Assert.False(deleting.IsCompleted);
        storage.Release();
        acquisition.Release();
        await WaitAsync(deleting);
        await WaitAsync(rig.Context.Deactivated);
        AssertDisposed(rig.Outbox, 1);
        var recovered = await rig.Receiver.GetSnapshotAsync();
        Assert.Empty(recovered.Effects);
        Assert.Equal(0, recovered.InboxCount);
        Assert.Equal(0, recovered.ProcessedMessageCount);
        Assert.Equal(0, recovered.OutboxCount);
    }

    private async Task<Rig> CreateAsync()
    {
        var receiver = NewGrain();
        await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var services = context.ActivationServices;
        var effects = services.GetRequiredKeyedService<IDurableDictionary<Guid, DurableEffect>>("test-effects");
        var handler = new TestHandler(effects);
        var rig = new Rig(receiver, context, Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance),
            services.GetRequiredService<IJournaledStateManager>(), (JournaledTestOutbox)services.GetRequiredService<IDurableOutbox>(),
            effects, handler,
            services.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>("__orleans.durable-messaging.inbox"),
            services.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DateTimeOffset>>("__orleans.durable-messaging.inbox-processed"));
        await OnTurnAsync(context, () => services.GetRequiredService<IDurableInbox>().RegisterHandler("async/handler", handler));
        return rig;
    }

    private async Task<EnvelopeLease> DeliverAsync(Rig rig)
    {
        var input = CreateEnvelope(rig.Receiver, NewMessage(501, "async-handler"), "async/handler");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, input.Value)).Status);
        await WaitAsync(rig.Handler.Entered.Task);
        return input;
    }

    private static DurableEnvelope CreateOutput(Rig rig) => new DurableEnvelopeBuilder(
        rig.Context.ActivationServices.GetRequiredService<SerializerSessionPool>(), rig.Receiver.GetGrainId())
        .To(rig.Receiver.GetGrainId(), "output").WithBody(41).Build();

    private static void InvokeContextOperation(TestHandler handler, string operation)
    {
        switch (operation)
        {
            case "complete": handler.Context.Complete(); break;
            case "send": handler.Context.Send(handler.Output); break;
            case "prepare": _ = handler.Context.Outbox.PrepareSendAsync([handler.Output]); break;
            default: throw new ArgumentException(nameof(operation));
        }
    }

    private static void AssertCompletedState(Rig rig, DurableEnvelope input)
    {
        Assert.Empty(rig.Inbox);
        Assert.Equal((input.SenderId, input.MessageId), Assert.Single(rig.Processed).Key);
    }

    private static void AssertSuccess(Rig rig, DurableEnvelope input, int outputCount)
    {
        AssertCompletedState(rig, input);
        Assert.Equal(new DurableEffect(input.MessageId, 1, 501, "async-handler"), Assert.Single(rig.Effects).Value);
        Assert.Equal(outputCount, rig.Outbox.Count);
        Assert.Empty(rig.Grain.GetSnapshotForTest().InboxDeadLetters);
        Assert.False(rig.Grain.DeactivationFailure.Task.IsCompleted);
    }

    private int Writes(Rig rig) => Fixture.Storage.GetSuccessfulWriteCount(rig.Journal);
    private static async Task AssertHealthyAsync(Rig rig)
    {
        await OnTurnAsync(rig.Context, async () =>
        {
            rig.Context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "healthy";
            await rig.Manager.WriteStateAsync(CancellationToken.None);
        });
        Assert.False(rig.Grain.DeactivationFailure.Task.IsCompleted);
    }

    private static async Task DeactivateAsync(Rig rig)
    {
        await rig.Receiver.RequestDeactivationAsync();
        await WaitAsync(rig.Context.Deactivated);
    }

    private async Task AssertFailureReplayAsync(Rig rig, DurableEnvelope input)
    {
        await WaitAsync(rig.Context.Deactivated);
        await rig.Receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForDeadLetterCountAsync(rig.Receiver, 1);
        Assert.Empty(recovered.Effects);
        Assert.Equal(0, recovered.OutboxCount);
        Assert.Equal(1, recovered.ProcessedMessageCount);
        Assert.Equal(input.MessageId, Assert.Single(recovered.InboxDeadLetters).MessageId);
    }

    private static void AssertDisposed(JournaledTestOutbox outbox, int expected)
    {
        Assert.Equal(expected, outbox.PreparedBatches.Count);
        Assert.All(outbox.PreparedBatches, batch => Assert.Equal(1, batch.DisposeCalls));
    }

    private static async Task<Task> FinishedAsync(Rig rig)
    {
        Task result = null!;
        await OnTurnAsync(rig.Context, () =>
        {
            var extension = rig.Context.ActivationServices.GetRequiredService(CancellationCleanupProbe.ExtensionType);
            var pending = CancellationCleanupProbe.Field<System.Collections.IList>(extension, "_pendingWrites");
            var operation = Assert.Single(pending.Cast<object>(), item => item.GetType().Name == "HandlerWrite");
            result = ((TaskCompletionSource)operation.GetType().GetProperty("Finished")!.GetValue(operation)!).Task;
        });
        return result;
    }

    private static IGrainTimer GetTimer(DiagnosticEventCollector events, Rig rig) =>
        Assert.Single(events.Events.Select(item => item.Payload).OfType<GrainTimerEvents.Created>(),
            item => ReferenceEquals(item.GrainContext, rig.Context) && IsInboxTimer(item.Timer)).Timer;
    private static bool IsInboxTimer(IGrainTimer timer) => timer.GetType().GenericTypeArguments is [var type]
        && type.DeclaringType == ReceiverTestServices.GetImplementationType("DurableInboxExtension");
    private static Task<DiagnosticEvent> TimerStoppedAsync(DiagnosticEventCollector events, IGrainTimer timer) =>
        events.WaitForEventAsync(nameof(GrainTimerEvents.TickStop),
            item => item.Payload is GrainTimerEvents.TickStop stop && ReferenceEquals(stop.Timer, timer),
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    private async Task<DurableJobRunResult> RunPumpAsync(Rig rig)
    {
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        var job = Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(ReceiverTestServices.InboxJobName, rig.Receiver.GetGrainId()));
        var run = new JobContext(job);
        var feature = (IDurableJobFeatureHandler)rig.Context.ActivationServices.GetRequiredService(CancellationCleanupProbe.ExtensionType);
        await OnTurnAsync(rig.Context, async () => Assert.True(
            (await feature.ExecuteJobAsync(run, TestContext.Current.CancellationToken)).IsInProgress));
        var timer = GetTimer(events, rig);
        await TimerStoppedAsync(events, timer);
        DurableJobRunResult result = null!;
        await OnTurnAsync(rig.Context, async () => result = await feature.ExecuteJobAsync(run, TestContext.Current.CancellationToken));
        return result;
    }

    private static Task OnTurnAsync(IGrainContext context, Action action) =>
        OnTurnAsync(context, () => { action(); return Task.CompletedTask; });
    private static Task OnTurnAsync(IGrainContext context, Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() => { _ = CompleteAsync(); });
        return done.Task;
        async Task CompleteAsync()
        {
            try
            {
                Assert.Same(context, ReceiverTestServices.CurrentGrainContext);
                await action();
                done.SetResult();
            }
            catch (Exception error) { done.SetException(error); }
        }
    }

    private static Task WaitAsync(Task task) => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    private static Task<T> WaitAsync<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    private sealed record Rig(IDurableMessagingTestGrain Receiver, IGrainContext Context, DurableMessagingTestGrain Grain,
        IJournaledStateManager Manager, JournaledTestOutbox Outbox, IDurableDictionary<Guid, DurableEffect> Effects,
        TestHandler Handler, IDurableDictionary<(GrainId, Guid), DurableEnvelope> Inbox,
        IDurableDictionary<(GrainId, Guid), DateTimeOffset> Processed)
    {
        public JournalId Journal => JournalId.FromGrainId(Receiver.GetGrainId());
    }

    private sealed class TestHandler(IDurableDictionary<Guid, DurableEffect> effects) : IInboxHandler, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IInboxHandlerContext Context { get; private set; } = null!;
        public DurableEnvelope Output { get; private set; }
        public DurableEnvelope UnusedOutput { get; private set; }
        public Func<TestHandler, CancellationToken, ValueTask> Body { get; set; } =
            static (self, _) => { self.Mutate(); self.Context.Complete(); return default; };
        public bool CanHandle(IInboxHandlerContext context) => false;
        public async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            Context = context;
            Output = context.CreateEnvelope().To(context.GrainId, "output").WithBody(41).Build();
            UnusedOutput = context.CreateEnvelope().To(context.GrainId, "unused").WithBody(42).Build();
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await Body(this, cancellationToken);
        }
        public void Mutate() => effects[Context.Envelope.MessageId] =
            new DurableEffect(Context.Envelope.MessageId, 1, 501, "async-handler");
        public void Dispose() => Release.TrySetResult();
    }
    private sealed class JobContext(DurableJob job) : IJobRunContext
    {
        public DurableJob Job { get; } = job;
        public string RunId { get; } = Guid.NewGuid().ToString("N");
        public int DequeueCount => 1;
    }
}
