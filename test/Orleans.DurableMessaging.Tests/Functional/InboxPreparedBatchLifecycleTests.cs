using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

// The controlled outbox observes local capabilities only. All effects, inbox accounting,
// capture, acknowledgement and replay use the real journal and receiver runtime.
[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class InboxPreparedBatchLifecycleTests : DurableMessagingBehaviorTestBase
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PreparationDuringAction_IsTerminalEvenWhenCaughtOrReplaced(bool caught, bool replaced)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        Exception? rejection = null;
        var replacement = new IOException("Replacement action failure must not win.");
        handler.Remainder = (self, _) => ValueTask.FromResult<Action>(() =>
        {
            self.ApplyEffect();
            try { PrepareInSynchronousPhase(self.Context, self.Output); }
            catch (InvalidOperationException exception)
            {
                rejection = exception;
                if (!caught) throw;
                if (replaced) throw replacement;
            }
        });
        using var input = await DeliverToHandlerAsync(rig);
        var writes = WriteCount(rig);
        handler.Continue.TrySetResult();
        var failure = await WaitAsync(rig.Grain.Faulted.Task);
        Assert.Same(Assert.IsType<InvalidOperationException>(rejection), failure);
        Assert.NotSame(replacement, failure);
        Assert.Equal(1, handler.Applied);
        Assert.Equal(1, rig.Outbox.PreparationsStarted);
        Assert.Equal(1, rig.Outbox.PreparationsCompleted);
        Assert.Equal(ExpectedEffect(input.Value), Assert.Single(rig.Effects).Value);
        await AssertFaultReplayAsync(rig, input.Value, failure, writes);
    }

    [Fact]
    public async Task RetainedFacade_CannotPrepareAfterAttempt_AndManagerRemainsHealthy()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        using var input = await DeliverToHandlerAsync(rig);
        var ack = ObserveNextAck(rig);
        handler.Continue.TrySetResult();
        var acknowledged = await WaitAsync(ack);
        AssertAcknowledgedIds(acknowledged, input.Value);
        AssertSuccess(acknowledged.Snapshot, input.Value, outputCount: 0);
        // A subsequent handler's entry is a public, deterministic prior-retirement barrier.
        using var next = new LifecycleHandler(rig.Effects, acquireFirst: false);
        await RegisterAsync(rig, "prepared/next", next);
        using var nextInput = CreateEnvelope(rig.Receiver, NewMessage(402, "next"), "prepared/next");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, nextInput.Value)).Status);
        await WaitAsync(next.Ready.Task);
        Assert.Equal(1, Assert.Single(rig.Outbox.PreparedBatches).DisposeCalls);
        var count = rig.Outbox.PreparationsStarted;
        var rejection = await Assert.ThrowsAsync<InvalidOperationException>(() => OnTurnAsync(rig.Context, async () =>
        {
            await handler.Context.Outbox.PrepareSendAsync([handler.Output]);
        }));
        Assert.Contains("PrepareAsync", rejection.Message, StringComparison.Ordinal);
        Assert.Equal(count, rig.Outbox.PreparationsStarted);
        await AssertHealthyWriteAsync(rig);
        next.Continue.TrySetResult();
        var completed = await Fixture.WaitForEffectCountAsync(rig.Receiver, 2);
        Assert.Equal(2, completed.ProcessedMessageCount);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(new[] { input.Value.MessageId, nextInput.Value.MessageId }.Order(),
            completed.Effects.Select(effect => effect.LogicalId).Order());
        Assert.All(completed.Effects, effect => Assert.Equal(1, effect.Count));
        Assert.Equal(0, rig.Outbox.SendCalls);
        Assert.Equal(0, completed.OutboxCount);
        Assert.Empty(completed.InboxDeadLetters);
        Assert.Empty(completed.OutboxDeadLetters);
        Assert.Equal(new[] { (input.Value.SenderId, input.Value.MessageId), (nextInput.Value.SenderId, nextInput.Value.MessageId) }
            .OrderBy(key => key.MessageId), rig.ProcessedState.Select(entry => entry.Key).OrderBy(key => key.MessageId));
        await DeactivateAsync(rig);
        AssertDisposed(rig.Outbox, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedFacade_CannotPrepareDuringAnotherAttemptEvenWhenCaught(bool caught)
    {
        var rig = await CreateAsync();
        using var first = rig.Handler;
        first.Remainder = (_, _) => ValueTask.FromResult<Action>(() => { });
        using var one = await DeliverToHandlerAsync(rig);
        var ack = ObserveNextAck(rig);
        first.Continue.TrySetResult();
        AssertAcknowledgedIds(await WaitAsync(ack), one.Value);
        using var second = new LifecycleHandler(rig.Effects);
        Exception? rejection = null;
        second.Remainder = async (self, token) =>
        {
            try { await first.Context.Outbox.PrepareSendAsync([self.Output], token); }
            catch (InvalidOperationException exception)
            {
                rejection = exception;
                if (!caught) throw;
            }
            return self.ApplyEffect;
        };
        await RegisterAsync(rig, "prepared/next", second);
        using var two = CreateEnvelope(rig.Receiver, NewMessage(403, "second"), "prepared/next");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, two.Value)).Status);
        await WaitAsync(second.Ready.Task);
        Assert.Equal(1, rig.Outbox.PreparedBatches[0].DisposeCalls);
        var writes = WriteCount(rig);
        second.Continue.TrySetResult();
        var failure = await WaitAsync(rig.Grain.Faulted.Task);
        Assert.Same(Assert.IsType<InvalidOperationException>(rejection), failure);
        Assert.Equal(0, second.Applied);
        Assert.Equal(2, rig.Outbox.PreparationsStarted);
        Assert.Equal(2, rig.Outbox.PreparationsCompleted);
        Assert.Empty(rig.Effects);
        await AssertFaultReplayAsync(rig, two.Value, failure, writes, processedBefore: 1, expectedBatchCount: 2);
    }

    [Fact]
    public async Task SelectionPreparation_IsRejectedBeforeProviderAcquisition()
    {
        var rig = await CreateAsync(register: false);
        using var handler = rig.Handler;
        var selection = new PreparingSelectionHandler();
        await OnTurnAsync(rig.Context, () =>
            rig.Context.ActivationServices.GetRequiredService<IDurableInbox>().RegisterHandler(selection));
        using var input = CreateEnvelope(rig.Receiver, NewMessage(404, "selection"), "prepared/selection");
        var rejection = await Assert.ThrowsAsync<InvalidOperationException>(() => DeliverAsync(rig.Receiver, input.Value));
        Assert.IsType<InvalidOperationException>(selection.Rejection);
        Assert.Contains("selection is read-only", rejection.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, selection.PrepareCalls);
        Assert.Equal(0, rig.Outbox.PreparationsStarted);
        Assert.Equal(0, rig.Outbox.PreparationsCompleted);
        Assert.Empty(rig.Outbox.PreparedBatches);
        Assert.Equal(0, rig.Outbox.SendCalls);
        Assert.Empty(rig.Effects);
        Assert.Empty(rig.Outbox);
        Assert.Equal(0, rig.Grain.GetSnapshotForTest().InboxCount);
        await AssertHealthyWriteAsync(rig);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncaughtPreparationFailure_RetainsAcquiredBatchUntilAccountingAck(bool providerFailure)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var failure = new IOException(providerFailure ? "Second preparation failed." : "Ordinary handler preparation failed.");
        handler.Remainder = async (self, token) =>
        {
            if (providerFailure) await self.Context.Outbox.PrepareSendAsync([self.UnusedOutput], token);
            throw failure;
        };
        using var input = await DeliverToHandlerAsync(rig);
        using var preparation = providerFailure ? rig.Outbox.BlockNextPreparation() : null;
        var storage = Fixture.Storage.BlockWrite(rig.JournalId);
        var ack = ObserveNextAck(rig);
        try
        {
            handler.Continue.TrySetResult();
            if (preparation is not null)
            {
                await preparation.WaitAsync();
                preparation.Fail(failure);
            }
            await storage.WaitUntilEnteredAsync();
            Assert.Equal(0, handler.Applied);
            Assert.Equal(0, rig.Outbox.SendCalls);
            Assert.Empty(rig.Outbox);
            Assert.Empty(rig.Effects);
            Assert.Equal(0, Assert.Single(rig.Outbox.PreparedBatches).DisposeCalls);
            Assert.False(ack.IsCompleted);
        }
        finally { storage.Release(); }
        var acknowledged = await WaitAsync(ack);
        AssertAcknowledgedIds(acknowledged, input.Value);
        Assert.Equal(new[] { 0 }, acknowledged.DisposeCalls);
        AssertDeadLetter(acknowledged.Snapshot, input.Value, failure.Message, attempts: 1);
        Assert.Equal(providerFailure ? 2 : 1, rig.Outbox.PreparationsStarted);
        Assert.Equal(rig.Outbox.PreparationsStarted, rig.Outbox.PreparationsCompleted);
        if (preparation is not null) Assert.Same(failure, preparation.Operation!.Failure);
        await AssertHealthyWriteAsync(rig);
        await DeactivateAsync(rig);
        AssertDisposed(rig.Outbox, 1);
        AssertDeadLetter(await rig.Receiver.GetSnapshotAsync(), input.Value, failure.Message, attempts: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaughtPreparationFailure_AllowsSafeAlternativeAndUnusedBatchRetires(bool acquireFirst)
    {
        var rig = await CreateAsync(acquireFirst);
        using var handler = rig.Handler;
        var failure = new IOException("Provider acquisition failed, not an API violation.");
        Exception? caught = null;
        handler.Remainder = async (self, token) =>
        {
            try { await self.Context.Outbox.PrepareSendAsync([self.UnusedOutput], token); }
            catch (IOException exception) { caught = exception; }
            return self.ApplyEffect;
        };
        using var input = await DeliverToHandlerAsync(rig);
        using var preparation = rig.Outbox.BlockNextPreparation();
        var storage = Fixture.Storage.BlockWrite(rig.JournalId);
        var ack = ObserveNextAck(rig);
        try
        {
            handler.Continue.TrySetResult();
            await preparation.WaitAsync();
            preparation.Fail(failure);
            await storage.WaitUntilEnteredAsync();
            Assert.Same(failure, caught);
            Assert.All(rig.Outbox.PreparedBatches, batch => Assert.Equal(0, batch.DisposeCalls));
            Assert.Equal(1, handler.Applied);
            Assert.Equal(0, rig.Outbox.SendCalls);
            Assert.Empty(rig.Outbox);
            Assert.False(ack.IsCompleted);
        }
        finally { storage.Release(); }
        var acknowledged = await WaitAsync(ack);
        AssertAcknowledgedIds(acknowledged, input.Value);
        Assert.Equal(acquireFirst ? new[] { 0 } : Array.Empty<int>(), acknowledged.DisposeCalls);
        AssertSuccess(acknowledged.Snapshot, input.Value, outputCount: 0);
        Assert.Same(failure, preparation.Operation!.Failure);
        Assert.Equal(acquireFirst ? 2 : 1, rig.Outbox.PreparationsStarted);
        Assert.Equal(rig.Outbox.PreparationsStarted, rig.Outbox.PreparationsCompleted);
        await AssertHealthyWriteAsync(rig);
        await DeactivateAsync(rig);
        AssertDisposed(rig.Outbox, acquireFirst ? 1 : 0);
        AssertSuccess(await rig.Receiver.GetSnapshotAsync(), input.Value, outputCount: 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulAction_RetainsUsedAndUnusedBatchesThroughActualAck(bool throughOutbox)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Remainder = async (self, token) =>
        {
            self.UnusedBatch = await self.Context.Outbox.PrepareSendAsync([self.UnusedOutput], token);
            return () =>
            {
                self.ApplyEffect();
                self.Send(self.Batch!, throughOutbox);
                self.Send(self.Batch!, throughOutbox); // Same live batch, same Action: idempotent staging.
            };
        };
        using var input = await DeliverToHandlerAsync(rig);
        var storage = Fixture.Storage.BlockWrite(rig.JournalId);
        var ack = ObserveNextAck(rig);
        try
        {
            handler.Continue.TrySetResult();
            await storage.WaitUntilEnteredAsync();
            Assert.Equal(1, handler.Applied);
            Assert.Equal(2, rig.Outbox.SendCalls);
            Assert.Equal(handler.Output.MessageId, Assert.Single(rig.Outbox).Key);
            Assert.Equal(handler.Output.MessageId, Assert.Single(rig.Outbox.LastCapturedIds));
            Assert.Equal(2, rig.Outbox.PreparedBatches.Count);
            Assert.True(rig.Outbox.PreparedBatches[0].IsStaged);
            Assert.False(rig.Outbox.PreparedBatches[1].IsStaged);
            Assert.All(rig.Outbox.PreparedBatches, batch => Assert.Equal(0, batch.DisposeCalls));
            Assert.False(ack.IsCompleted);
        }
        finally { storage.Release(); }
        var acknowledged = await WaitAsync(ack);
        AssertAcknowledgedIds(acknowledged, input.Value);
        Assert.Equal(new[] { 0, 0 }, acknowledged.DisposeCalls);
        AssertSuccess(acknowledged.Snapshot, input.Value, outputCount: 1);
        Assert.Equal(handler.Output.MessageId, Assert.Single(rig.Outbox.PreparedBatches[0].MessageIds));
        Assert.Equal(handler.UnusedOutput.MessageId, Assert.Single(rig.Outbox.PreparedBatches[1].MessageIds));
        Assert.Equal(2, rig.Outbox.PreparationsStarted);
        Assert.Equal(2, rig.Outbox.PreparationsCompleted);
        await AssertHealthyWriteAsync(rig);
        await DeactivateAsync(rig);
        AssertDisposed(rig.Outbox, 2);
        AssertSuccess(await rig.Receiver.GetSnapshotAsync(), input.Value, outputCount: 1);
        Assert.Equal(handler.Output.MessageId, Assert.Single(Fixture.GetStagedOutput(rig.Receiver)).MessageId);
    }

    [Fact]
    public async Task ThrowingActionBeforeSend_RetiresBatchAndReplaysOriginalCause()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var failure = new IOException("Action failed before sending.");
        handler.Remainder = (_, _) => ValueTask.FromResult<Action>(() => throw failure);
        using var input = await DeliverToHandlerAsync(rig);
        var writes = WriteCount(rig);
        handler.Continue.TrySetResult();
        Assert.Same(failure, await WaitAsync(rig.Grain.Faulted.Task));
        Assert.Equal(1, handler.Applied);
        Assert.Empty(rig.Effects);
        await AssertFaultReplayAsync(rig, input.Value, failure, writes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UnderlyingSendFailure_FirstExceptionWinsEvenWhenCaughtOrReplaced(bool throughOutbox, bool replaced)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var failure = new IOException("Synchronous staging failed.");
        var replacement = new IOException("Replacement must not become the terminal cause.");
        Exception? caught = null;
        handler.Remainder = (self, _) => ValueTask.FromResult<Action>(() =>
        {
            self.ApplyEffect();
            try { self.Send(self.Batch!, throughOutbox); }
            catch (IOException exception) { caught = exception; }
            if (replaced) throw replacement;
        });
        using var input = await DeliverToHandlerAsync(rig);
        await OnTurnAsync(rig.Context, () => rig.Outbox.NextSendFailure = failure);
        var writes = WriteCount(rig);
        handler.Continue.TrySetResult();
        Assert.Same(failure, await WaitAsync(rig.Grain.Faulted.Task));
        Assert.Same(failure, caught);
        Assert.Equal(1, handler.Applied);
        Assert.Equal(ExpectedEffect(input.Value), Assert.Single(rig.Effects).Value);
        Assert.Equal(1, rig.Outbox.SendCalls);
        Assert.Null(rig.Outbox.NextSendFailure);
        Assert.False(Assert.Single(rig.Outbox.PreparedBatches).IsStaged);
        await AssertFaultReplayAsync(rig, input.Value, failure, writes, expectedSendCalls: 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ForgottenPreparation_RejectsBeforeActionAndDrainsLateProviderOutcome(bool acquireFirst, bool providerFails)
    {
        var rig = await CreateAsync(acquireFirst);
        using var handler = rig.Handler;
        handler.Remainder = (self, token) =>
        {
            _ = self.Context.Outbox.PrepareSendAsync([self.UnusedOutput], token);
            return ValueTask.FromResult<Action>(self.ApplyEffect);
        };
        using var input = await DeliverToHandlerAsync(rig);
        using var preparation = rig.Outbox.BlockNextPreparation(ignoreCancellation: true);
        var providerFailure = new IOException("Late unawaited provider failure.");
        var writes = WriteCount(rig);
        handler.Continue.TrySetResult();
        await preparation.WaitAsync();
        var failure = Assert.IsType<InvalidOperationException>(await WaitAsync(rig.Grain.Faulted.Task));
        Assert.Equal("Inbox handlers must await every batch preparation before returning their synchronous apply action.", failure.Message);
        Assert.Same(failure, rig.Outbox.Failure);
        Assert.Equal(0, handler.Applied);
        Assert.False(preparation.Operation!.Completed.IsCompleted);
        Assert.False(rig.Context.Deactivated.IsCompleted);
        Assert.Equal(acquireFirst ? 2 : 1, rig.Outbox.PreparationsStarted);
        Assert.Equal(acquireFirst ? 1 : 0, rig.Outbox.PreparationsCompleted);
        Assert.All(rig.Outbox.PreparedBatches, batch => Assert.Equal(0, batch.DisposeCalls));
        if (providerFails) preparation.Fail(providerFailure);
        else preparation.Release();
        await WaitAsync(preparation.Operation.Completed);
        if (providerFails) Assert.Same(providerFailure, preparation.Operation.Failure);
        else
        {
            Assert.Null(preparation.Operation.Failure);
            Assert.Equal(handler.UnusedOutput.MessageId, Assert.Single(preparation.Operation.Batch!.MessageIds));
        }
        await AssertFaultReplayAsync(rig, input.Value, failure, writes,
            expectedBatchCount: (acquireFirst ? 1 : 0) + (providerFails ? 0 : 1));
        Assert.Equal(acquireFirst ? 2 : 1, rig.Outbox.PreparationsCompleted);
        Assert.Equal((acquireFirst ? 1 : 0) + (providerFails ? 0 : 1), rig.Outbox.PreparedBatches.Count);
        Assert.All(rig.Outbox.PreparedBatches, batch => Assert.False(batch.IsStaged));
        Assert.Same(failure, rig.Outbox.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingPreparation_AfterTerminalMisuse_DrainsWithoutReplacingOriginalCause(bool providerFails)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        Exception? rejection = null;
        var replacement = new IOException("Ordinary failure after terminal misuse.");
        handler.Remainder = (self, token) =>
        {
            _ = self.Context.Outbox.PrepareSendAsync([self.UnusedOutput], token);
            try { self.Context.Send(self.Batch!); }
            catch (InvalidOperationException exception) { rejection = exception; }
            throw replacement;
        };
        using var input = await DeliverToHandlerAsync(rig);
        using var preparation = rig.Outbox.BlockNextPreparation(ignoreCancellation: true);
        var providerFailure = new IOException("Late acquisition failure must not replace misuse.");
        var writes = WriteCount(rig);
        handler.Continue.TrySetResult();
        await preparation.WaitAsync();
        var failure = await WaitAsync(rig.Grain.Faulted.Task);
        Assert.Same(Assert.IsType<InvalidOperationException>(rejection), failure);
        Assert.Contains("synchronous apply action", failure.Message, StringComparison.Ordinal);
        Assert.NotSame(replacement, failure);
        Assert.Equal(0, handler.Applied);
        Assert.False(preparation.Operation!.Completed.IsCompleted);
        Assert.False(rig.Context.Deactivated.IsCompleted);
        Assert.Equal(0, Assert.Single(rig.Outbox.PreparedBatches).DisposeCalls);
        if (providerFails) preparation.Fail(providerFailure);
        else preparation.Release();
        await WaitAsync(preparation.Operation.Completed);
        if (providerFails) Assert.Same(providerFailure, preparation.Operation.Failure);
        else Assert.Null(preparation.Operation.Failure);
        await AssertFaultReplayAsync(rig, input.Value, failure, writes, expectedBatchCount: providerFails ? 1 : 2);
        Assert.Equal(2, rig.Outbox.PreparationsStarted);
        Assert.Equal(2, rig.Outbox.PreparationsCompleted);
        Assert.Equal(providerFails ? 1 : 2, rig.Outbox.PreparedBatches.Count);
        Assert.Same(failure, rig.Outbox.Failure);
    }

    [Theory]
    [InlineData(false, "foreign")]
    [InlineData(true, "foreign")]
    [InlineData(false, "disposed")]
    [InlineData(true, "disposed")]
    [InlineData(false, "wrong-attempt")]
    [InlineData(true, "wrong-attempt")]
    public async Task CurrentFacade_InvalidBatchIsTerminalAndRespectsApplicationOwnership(bool throughOutbox, string kind)
    {
        var rig = await CreateAsync();
        using var first = rig.Handler;
        IPreparedOutboxBatch? invalid = null;
        JournaledTestOutbox.BatchObservation? applicationObservation = null;
        LifecycleHandler current = first;
        var processedBefore = 0;
        if (kind == "foreign")
        {
            using var applicationOutput = CreateEnvelope(rig.Receiver, NewMessage(405, "application-owned"), "output");
            await OnTurnAsync(rig.Context, async () =>
            {
                invalid = await rig.Outbox.PrepareSendAsync([applicationOutput.Value]);
            });
            applicationObservation = Assert.Single(rig.Outbox.PreparedBatches);
        }
        else if (kind == "wrong-attempt")
        {
            first.Remainder = (_, _) => ValueTask.FromResult<Action>(() => { });
            using var one = await DeliverToHandlerAsync(rig);
            var ack = ObserveNextAck(rig);
            first.Continue.TrySetResult();
            AssertAcknowledgedIds(await WaitAsync(ack), one.Value);
            invalid = first.Batch;
            current = new LifecycleHandler(rig.Effects);
            await RegisterAsync(rig, "prepared/next", current);
            processedBefore = 1;
        }
        try
        {
            using var currentLease = current;
            Exception? rejection = null;
            current.Remainder = (self, _) => ValueTask.FromResult<Action>(() =>
            {
                if (kind == "disposed")
                {
                    invalid = self.Batch!;
                    invalid.Dispose();
                }
                try { self.Send(invalid!, throughOutbox); }
                catch (Exception exception) { rejection = exception; }
                self.ApplyEffect(); // Catching capability misuse cannot make this durable.
            });
            using var input = CreateEnvelope(rig.Receiver, NewMessage(406, "invalid-capability"),
                kind == "wrong-attempt" ? "prepared/next" : "prepared/lifecycle");
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, input.Value)).Status);
            await WaitAsync(current.Ready.Task);
            if (kind == "wrong-attempt") Assert.Equal(1, rig.Outbox.PreparedBatches[0].DisposeCalls);
            var writes = WriteCount(rig);
            current.Continue.TrySetResult();
            var failure = await WaitAsync(rig.Grain.Faulted.Task);
            if (kind == "disposed") Assert.IsType<ObjectDisposedException>(rejection);
            else Assert.IsType<InvalidOperationException>(rejection);
            Assert.Same(rejection, failure);
            Assert.Same(failure, rig.Outbox.Failure);
            Assert.Equal(1, current.Applied);
            Assert.Equal(ExpectedEffect(input.Value), Assert.Single(rig.Effects).Value);
            Assert.Equal(0, rig.Outbox.SendCalls);
            Assert.Equal(writes, WriteCount(rig));
            Assert.Empty(rig.Outbox);
            Assert.Equal((input.Value.SenderId, input.Value.MessageId), Assert.Single(rig.InboxState).Key);
            Assert.Equal(processedBefore, rig.ProcessedState.Count);
            Assert.False(rig.ProcessedState.ContainsKey((input.Value.SenderId, input.Value.MessageId)));
            await WaitAsync(rig.Context.Deactivated);
            foreach (var observation in rig.Outbox.PreparedBatches)
                Assert.Equal(ReferenceEquals(observation, applicationObservation) ? 0 : 1, observation.DisposeCalls);
            if (applicationObservation is not null) Assert.False(applicationObservation.IsStaged);
            var expectedPreparations = kind == "disposed" ? 1 : 2;
            Assert.Equal(expectedPreparations, rig.Outbox.PreparationsStarted);
            Assert.Equal(expectedPreparations, rig.Outbox.PreparationsCompleted);
            Assert.Equal(expectedPreparations, rig.Outbox.PreparedBatches.Count);
            Assert.All(rig.Outbox.PreparedBatches, batch => Assert.False(batch.IsStaged));
            await rig.Receiver.GetSnapshotAsync();
            var recovered = await Fixture.WaitForDeadLetterCountAsync(rig.Receiver, 1);
            AssertDeadLetter(recovered, input.Value, processedBefore: processedBefore);
            Assert.True(Processed(Fixture.GetGrainContext(rig.Receiver)).ContainsKey((input.Value.SenderId, input.Value.MessageId)));
            Assert.Empty(Fixture.GetStagedOutput(rig.Receiver));
        }
        finally
        {
            // Only the application owns a raw batch; runtime wrapper handles are retired by the inbox.
            if (applicationObservation is not null) invalid!.Dispose();
        }
        if (applicationObservation is not null) Assert.Equal(1, applicationObservation.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedHandlerCancellation_DrainsLateAcquisitionAndPreservesCancellation(bool providerFails)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        using var input = CreateEnvelope(rig.Receiver, NewMessage(407, "owned-cancellation"), "prepared/lifecycle");
        var job = new DurableJob
        {
            Id = "prepared-cancel-physical",
            ShardId = "prepared-cancel-shard",
            Name = ReceiverTestServices.InboxJobName,
            TargetGrainId = rig.Receiver.GetGrainId(),
            DueTime = Fixture.Clock.GetUtcNow(),
            Metadata = new Dictionary<string, string> { ["orleans.messaging.ownership-id"] = "prepared-cancel:1" }
        };
        await rig.Receiver.SeedInboxStateAsync(input.Value, "prepared-cancel:1", job);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Remainder = async (self, token) =>
        {
            using var observation = token.Register(() => cancellationObserved.TrySetResult());
            _ = self.Context.Outbox.PrepareSendAsync([self.UnusedOutput], token);
            await cancellationObserved.Task;
            token.ThrowIfCancellationRequested();
            return self.ApplyEffect;
        };
        var extension = (IDurableJobFeatureHandler)rig.Context.ActivationServices.GetRequiredService(
            ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        using var cancellation = new CancellationTokenSource();
        DurableJobRunResult result = null!;
        await OnTurnAsync(rig.Context, async () =>
            result = await extension.ExecuteJobAsync(new JobContext(job), cancellation.Token));
        Assert.Equal(DurableJobRunStatus.InProgress, result.Status);
        await WaitAsync(handler.Ready.Task);
        using var preparation = rig.Outbox.BlockNextPreparation(ignoreCancellation: true);
        var writes = WriteCount(rig);
        handler.Continue.TrySetResult();
        await preparation.WaitAsync();
        cancellation.Cancel();
        await WaitAsync(cancellationObserved.Task);
        var failure = await WaitAsync(rig.Grain.Faulted.Task);
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.False(preparation.Operation!.Completed.IsCompleted);
        Assert.False(rig.Context.Deactivated.IsCompleted);
        Assert.Equal(0, handler.Applied);
        Assert.Equal(0, rig.Outbox.SendCalls);
        Assert.Empty(rig.Outbox.Messages);
        Assert.Equal(0, Assert.Single(rig.Outbox.PreparedBatches).DisposeCalls);
        var lateFailure = new IOException("Provider failed after owned handler cancellation.");
        if (providerFails) preparation.Fail(lateFailure);
        else preparation.Release();
        await WaitAsync(preparation.Operation.Completed);
        Assert.NotSame(lateFailure, failure);
        if (providerFails) Assert.Same(lateFailure, preparation.Operation.Failure);
        else Assert.Null(preparation.Operation.Failure);
        await AssertFaultReplayAsync(rig, input.Value, failure, writes, expectedBatchCount: providerFails ? 1 : 2);
        Assert.Equal(2, rig.Outbox.PreparationsStarted);
        Assert.Equal(2, rig.Outbox.PreparationsCompleted);
    }

    private sealed class JobContext(DurableJob job) : IJobRunContext
    {
        public DurableJob Job { get; } = job;
        public string RunId { get; } = Guid.NewGuid().ToString("N");
        public int DequeueCount => 1;
    }

    private async Task<Harness> CreateAsync(bool acquireFirst = true, bool register = true)
    {
        var receiver = NewGrain();
        await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var services = context.ActivationServices;
        var effects = services.GetRequiredKeyedService<IDurableDictionary<Guid, DurableEffect>>("test-effects");
        var result = new Harness(receiver, context, Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance),
            services.GetRequiredService<IJournaledStateManager>(),
            (JournaledTestOutbox)services.GetRequiredService<IDurableOutbox>(), effects,
            new LifecycleHandler(effects, acquireFirst));
        if (register) await RegisterAsync(result, "prepared/lifecycle", result.Handler);
        return result;
    }

    private static Task RegisterAsync(Harness rig, string route, IInboxHandler handler) =>
        OnTurnAsync(rig.Context, () => rig.Context.ActivationServices.GetRequiredService<IDurableInbox>().RegisterHandler(route, handler));

    private async Task<EnvelopeLease> DeliverToHandlerAsync(Harness rig)
    {
        var input = CreateEnvelope(rig.Receiver, NewMessage(401, "prepared-lifecycle"), "prepared/lifecycle");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, input.Value)).Status);
        await WaitAsync(rig.Handler.Ready.Task);
        Assert.Equal((input.Value.SenderId, input.Value.MessageId), Assert.Single(rig.InboxState).Key);
        if (rig.Handler.Batch is not null)
        {
            var batch = Assert.Single(rig.Outbox.PreparedBatches);
            Assert.Equal(rig.Handler.Output.MessageId, Assert.Single(batch.MessageIds));
            Assert.False(batch.IsStaged);
            Assert.Equal(0, batch.DisposeCalls);
        }
        return input;
    }

    private static Task<Acknowledgement> ObserveNextAck(Harness rig)
    {
        var completion = new TaskCompletionSource<Acknowledgement>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Outbox.AfterWriteCompleted = () => completion.TrySetResult(new(
            rig.Grain.GetSnapshotForTest(), rig.Outbox.PreparedBatches.Select(batch => batch.DisposeCalls).ToArray(),
            rig.ProcessedState.Select(entry => entry.Key).ToArray(), rig.InboxState.Select(entry => entry.Key).ToArray()));
        return completion.Task;
    }

    private int WriteCount(Harness rig) => Fixture.Storage.GetSuccessfulWriteCount(rig.JournalId);

    private static async Task AssertHealthyWriteAsync(Harness rig)
    {
        await OnTurnAsync(rig.Context, async () =>
        {
            rig.Context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "healthy-after-preparation";
            await rig.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        });
        Assert.False(rig.Grain.Faulted.Task.IsCompleted);
        Assert.Null(rig.Outbox.Failure);
    }

    private static async Task DeactivateAsync(Harness rig)
    {
        await rig.Receiver.RequestDeactivationAsync();
        await WaitAsync(rig.Context.Deactivated);
    }

    private async Task AssertFaultReplayAsync(Harness rig, DurableEnvelope input, Exception failure, int writes,
        int processedBefore = 0, int expectedSendCalls = 0, int expectedBatchCount = 1)
    {
        Assert.Same(failure, rig.Outbox.Failure);
        Assert.Equal(writes, WriteCount(rig));
        Assert.Equal(expectedSendCalls, rig.Outbox.SendCalls);
        Assert.Empty(rig.Outbox);
        var live = rig.Grain.GetSnapshotForTest();
        Assert.Equal(1, live.InboxCount);
        Assert.Equal(processedBefore, live.ProcessedMessageCount);
        Assert.Empty(live.InboxDeadLetters);
        Assert.Equal((input.SenderId, input.MessageId), Assert.Single(rig.InboxState).Key);
        Assert.False(rig.ProcessedState.ContainsKey((input.SenderId, input.MessageId)));
        await WaitAsync(rig.Context.Deactivated);
        AssertDisposed(rig.Outbox, expectedBatchCount);
        await rig.Receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForDeadLetterCountAsync(rig.Receiver, 1);
        AssertDeadLetter(recovered, input, processedBefore: processedBefore);
        Assert.True(Processed(Fixture.GetGrainContext(rig.Receiver)).ContainsKey((input.SenderId, input.MessageId)));
        Assert.Empty(Fixture.GetStagedOutput(rig.Receiver));
        Assert.Same(failure, rig.Outbox.Failure);
    }

    private static void AssertDisposed(JournaledTestOutbox outbox, int expected)
    {
        Assert.Equal(expected, outbox.PreparedBatches.Count);
        Assert.All(outbox.PreparedBatches, batch =>
        {
            Assert.Equal(1, batch.DisposeCalls);
            Assert.Same(batch, outbox.Preparations.Single(operation => operation.Id == batch.PreparationId).Batch);
        });
    }

    private static IDurableDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> Processed(IGrainContext context) =>
        context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DateTimeOffset>>(
            "__orleans.durable-messaging.inbox-processed");

    private static IDurableDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> Inbox(IGrainContext context) =>
        context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>(
            "__orleans.durable-messaging.inbox");

    private static void AssertAcknowledgedIds(Acknowledgement acknowledgement, DurableEnvelope input)
    {
        Assert.Equal((input.SenderId, input.MessageId), Assert.Single(acknowledgement.ProcessedIds));
        Assert.Empty(acknowledgement.InboxIds);
    }

    private static void PrepareInSynchronousPhase(IInboxHandlerContext context, DurableEnvelope output)
    {
        // A guard can throw synchronously or return an already-faulted ValueTask. Never block
        // the activation if a regression incorrectly starts asynchronous provider acquisition.
        var preparation = context.Outbox.PrepareSendAsync([output]);
        Assert.True(preparation.IsCompleted, "A forbidden preparation must reject before provider acquisition.");
        preparation.GetAwaiter().GetResult();
    }

    private static DurableEffect ExpectedEffect(DurableEnvelope input) => new(input.MessageId, 1, 401, "prepared-lifecycle");

    private static void AssertSuccess(DurableEndpointSnapshot snapshot, DurableEnvelope input, int outputCount)
    {
        Assert.Equal(ExpectedEffect(input), Assert.Single(snapshot.Effects));
        Assert.Equal(0, snapshot.InboxCount);
        Assert.Equal(1, snapshot.ProcessedMessageCount);
        Assert.Equal(outputCount, snapshot.OutboxCount);
        Assert.Empty(snapshot.InboxDeadLetters);
        Assert.Empty(snapshot.OutboxDeadLetters);
    }

    private static void AssertDeadLetter(DurableEndpointSnapshot snapshot, DurableEnvelope input,
        string? reason = null, int? attempts = null, int processedBefore = 0)
    {
        Assert.Empty(snapshot.Effects);
        Assert.Equal(0, snapshot.InboxCount);
        Assert.Equal(0, snapshot.OutboxCount);
        Assert.Equal(processedBefore + 1, snapshot.ProcessedMessageCount);
        var deadLetter = Assert.Single(snapshot.InboxDeadLetters);
        Assert.Equal(input.MessageId, deadLetter.MessageId);
        Assert.Equal(input.RouteKey, deadLetter.Route);
        if (reason is not null) Assert.Equal(reason, deadLetter.Reason);
        if (attempts is not null) Assert.Equal(attempts.Value, deadLetter.AttemptCount);
        Assert.Empty(snapshot.OutboxDeadLetters);
    }

    private static Task WaitAsync(Task task) => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    private static Task<T> WaitAsync<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    private static Task OnTurnAsync(IGrainContext context, Action action) =>
        OnTurnAsync(context, () => { action(); return Task.CompletedTask; });

    private static Task OnTurnAsync(IGrainContext context, Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() => { _ = CompleteAsync(); });
        return completion.Task;

        async Task CompleteAsync()
        {
            try { await action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        }
    }

    private sealed record Acknowledgement(DurableEndpointSnapshot Snapshot, int[] DisposeCalls,
        (GrainId SenderId, Guid MessageId)[] ProcessedIds, (GrainId SenderId, Guid MessageId)[] InboxIds);
    private sealed record Harness(IDurableMessagingTestGrain Receiver, IGrainContext Context, DurableMessagingTestGrain Grain,
        IJournaledStateManager Manager, JournaledTestOutbox Outbox, IDurableDictionary<Guid, DurableEffect> Effects,
        LifecycleHandler Handler)
    {
        public JournalId JournalId => Orleans.Journaling.JournalId.FromGrainId(Receiver.GetGrainId());
        // Retain failed state instances before deactivation disposes the service scope.
        public IDurableDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> InboxState { get; } = Inbox(Context);
        public IDurableDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> ProcessedState { get; } = Processed(Context);
    }

    private sealed class LifecycleHandler(IDurableDictionary<Guid, DurableEffect> effects, bool acquireFirst = true) : IInboxHandler, IDisposable
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IInboxHandlerContext Context { get; private set; } = null!;
        public DurableEnvelope Output { get; private set; }
        public DurableEnvelope UnusedOutput { get; private set; }
        public IPreparedOutboxBatch? Batch { get; private set; }
        public IPreparedOutboxBatch? UnusedBatch { get; set; }
        public int Applied { get; private set; }
        public Func<LifecycleHandler, CancellationToken, ValueTask<Action>> Remainder { get; set; } =
            static (self, _) => ValueTask.FromResult<Action>(self.ApplyEffect);

        public bool CanHandle(IInboxHandlerContext context) => false; // Registered by exact route.
        public async ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            Context = context;
            Output = context.CreateEnvelope().To(context.GrainId, "output").WithBody(41).Build();
            UnusedOutput = context.CreateEnvelope().To(context.GrainId, "unused-output").WithBody(42).Build();
            if (acquireFirst) Batch = await context.Outbox.PrepareSendAsync([Output], cancellationToken);
            Ready.TrySetResult();
            await Continue.Task.WaitAsync(cancellationToken);
            var action = await Remainder(this, cancellationToken);
            return () => { Applied++; action(); };
        }

        public void ApplyEffect() => effects[Context.Envelope.MessageId] = ExpectedEffect(Context.Envelope);
        public void Send(IPreparedOutboxBatch batch, bool throughOutbox)
        {
            if (throughOutbox) Context.Outbox.Send(batch);
            else Context.Send(batch);
        }
        public void Dispose() => Continue.TrySetResult(); // Runtime, not handler, owns the batches.
    }

    private sealed class PreparingSelectionHandler : IInboxHandler
    {
        public Exception? Rejection { get; private set; }
        public int PrepareCalls { get; private set; }
        public bool CanHandle(IInboxHandlerContext context)
        {
            if (context.Envelope.RouteKey != "prepared/selection") return false;
            try { PrepareInSynchronousPhase(context, context.Envelope); }
            catch (InvalidOperationException exception) { Rejection = exception; throw; }
            return true;
        }
        public ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            PrepareCalls++;
            return ValueTask.FromResult<Action>(() => { });
        }
    }
}
