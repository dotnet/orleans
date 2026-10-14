using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Messaging.Configuration;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class InboxHandlerCompletionTests : MessagingBehaviorTestBase
{
    [Fact]
    public async Task DeadLetter_StagesReasonPayloadAndDedupeBeforeReturnAndPersistsAfterAcknowledgement()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var calls = 0;
        handler.Body = (self, _) =>
        {
            calls++;
            self.Context.Fail("unsupported input");
            AssertCompletedState(rig, self.Context.Message);
            var retained = Assert.Single(rig.Grain.GetSnapshotForTest().InboxDeadLetters);
            Assert.Equal("unsupported input", retained.Reason);
            Assert.Equal(1, retained.AttemptCount);
            Assert.Equal(self.Context.Message.Envelope.MessageId, retained.MessageId);
            Assert.Equal(new TestMessage(self.Context.Message.Envelope.MessageId, 501, "async-handler"),
                Assert.IsType<TestMessage>(TestApplicationProtocol.Read(
                    rig.Context.ActivationServices.GetRequiredService<SerializerSessionPool>(), self.Context.Message).Body));
            self.Context.Fail("unsupported input");
            self.Context.Complete();
            Assert.Equal(retained, Assert.Single(rig.Grain.GetSnapshotForTest().InboxDeadLetters));
            return ValueTask.CompletedTask;
        };
        var input = await DeliverAsync(rig);
        using var storage = Fixture.Storage.BlockAcknowledgement(rig.Journal);
        var finished = FinishedAsync(rig);
        const string counter = "orleans-messaging-inbox-messages-processed";
        var processed = Fixture.Metrics.WaitForCountAsync(counter, 1, "dead_lettered");
        handler.Release.TrySetResult();
        await storage.WaitUntilEnteredAsync();
        Assert.False(finished.IsCompleted);
        AssertCompletedState(rig, input);
        Assert.Empty(rig.Effects);
        Assert.Empty(rig.Outbox);
        storage.Release();
        await WaitAsync(finished);
        await processed;
        Assert.Equal(1, calls);
        Assert.Equal(1, Fixture.Metrics.GetCount(counter, "dead_lettered"));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, input)).Status);
        Assert.Equal(1, calls);
        await AssertHealthyAsync(rig);
        await DeactivateAsync(rig);
        var recovered = await rig.Receiver.GetSnapshotAsync();
        var letter = Assert.Single(recovered.InboxDeadLetters);
        Assert.Equal("unsupported input", letter.Reason);
        Assert.Equal(1, letter.AttemptCount);
        Assert.Equal(input.Envelope.MessageId, letter.MessageId);
        Assert.Empty(recovered.Effects);
        Assert.Equal(0, recovered.InboxCount);
        Assert.Equal(1, recovered.ProcessedMessageCount);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, input)).Status);
        var recoveredContext = Fixture.GetGrainContext(rig.Receiver);
        await OnTurnAsync(recoveredContext, async () =>
        {
            var diagnostics = recoveredContext.ActivationServices.GetRequiredService<IMessagingDiagnostics>();
            var retained = Assert.Single(diagnostics.InboxDeadLetters);
            Assert.Equal(input.Envelope.MessageId, retained.Message.Envelope.MessageId);
            Assert.Equal(input.Envelope.Subject, retained.Message.Envelope.Subject);
            Assert.Equal(input.Envelope.Payload, retained.Message.Envelope.Payload);
            var expectedHeaders = Assert.IsType<Dictionary<string, byte[]>>(input.Envelope.Headers);
            var retainedHeaders = Assert.IsType<Dictionary<string, byte[]>>(retained.Message.Envelope.Headers);
            Assert.Equal(expectedHeaders.Keys.Order(StringComparer.Ordinal), retainedHeaders.Keys.Order(StringComparer.Ordinal));
            foreach (var (name, bytes) in expectedHeaders)
            {
                Assert.Equal(bytes, retainedHeaders[name]);
            }
            Assert.True(diagnostics.RemoveInboxDeadLetter(input.Envelope.MessageId));
            await recoveredContext.ActivationServices.GetRequiredService<IJournaledStateManager>().WriteStateAsync();
            Assert.Empty(diagnostics.InboxDeadLetters);
        });
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, input)).Status);
    }

    [Collection(MessagingClusterCollection.Name)]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Messaging")]
    public sealed class InboxPermanentFailureTests() : MessagingBehaviorTestBase(new PermanentFailureClusterFixture())
    {
        [Fact]
        public async Task DeadLetter_AfterPreparationRetryTerminatesBeforeLimitAndCountsCurrentAttempt()
        {
            var receiver = NewGrain();
            await receiver.GetSnapshotAsync();
            var context = Fixture.GetGrainContext(receiver);
            var grain = Assert.IsType<MessagingTestGrain>(context.GrainInstance);
            var handler = new PermanentHandler();
            var installed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Scheduler.QueueAction(() =>
            {
                grain.HandlerOverride = handler;
                installed.SetResult();
            });
            await installed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var input = CreateEnvelope(receiver, NewMessage(901, "permanent input"), "permanent/input");
            const string counter = "orleans-messaging-inbox-messages-processed";
            var retried = Fixture.Metrics.WaitForCountAsync(counter, 1, "retry");
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, input)).Status);
            await retried;
            Assert.Equal(1, handler.Attempts);
            Assert.Equal(1, (await receiver.GetSnapshotAsync()).InboxCount);

            var terminated = Fixture.Metrics.WaitForCountAsync(counter, 1, "dead_lettered");
            Fixture.Clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, input)).Status);
            await terminated;
            var snapshot = await receiver.GetSnapshotAsync();
            Assert.Equal(2, handler.Attempts);
            Assert.Equal(0, snapshot.InboxCount);
            Assert.Equal(1, snapshot.ProcessedMessageCount);
            Assert.Empty(snapshot.Effects);
            var deadLetter = Assert.Single(snapshot.InboxDeadLetters);
            Assert.Equal(input.Envelope.MessageId, deadLetter.MessageId);
            Assert.Equal("permanent input", deadLetter.Reason);
            Assert.Equal(2, deadLetter.AttemptCount);
            Assert.Equal(1, Fixture.Metrics.GetCount(counter, "retry"));
            Assert.Equal(1, Fixture.Metrics.GetCount(counter, "dead_lettered"));
            Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, input)).Status);
            Assert.Equal(2, handler.Attempts);
        }

        private sealed class PermanentHandler : IInboxHandler
        {
            public int Attempts { get; private set; }

            public bool CanHandle(IInboxHandlerContext context) => true;


            public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
            {
                if (++Attempts == 1)
                {
                    throw new ArgumentException("preparation failed");
                }
                context.Fail("permanent input");
                return ValueTask.CompletedTask;
            }
        }

        private sealed class PermanentFailureClusterFixture : MessagingClusterFixture
        {
            protected override void ConfigureOptions(DurableMessagingOptions options)
            {
                base.ConfigureOptions(options);
                options.MaxProcessingAttempts = 5;
                options.RetryInitialInterval = TimeSpan.FromHours(1);
                options.DeduplicationWindow = TimeSpan.FromDays(7);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task DeadLetter_InvalidReasonLeavesTerminalStateUnchanged(string? reason)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Body = (self, _) =>
        {
            var error = Assert.ThrowsAny<ArgumentException>(() => self.Context.Fail(reason!));
            Assert.Equal("reason", error.ParamName);
            Assert.Single(rig.Inbox);
            Assert.Empty(rig.Processed);
            Assert.Empty(rig.Grain.GetSnapshotForTest().InboxDeadLetters);
            self.Mutate();
            self.Context.Complete();
            return ValueTask.CompletedTask;
        };
        var input = await DeliverAsync(rig);
        var finished = FinishedAsync(rig);
        handler.Release.TrySetResult();
        await WaitAsync(finished);
        AssertSuccess(rig, input, outputCount: 0);
        await AssertHealthyAsync(rig);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadLetter_ConflictingTerminalOutcomeRejectsAttemptAndDeactivates(bool completeFirst)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        Exception? expected = null;
        handler.Body = (self, _) =>
        {
            if (completeFirst) self.Context.Complete();
            else self.Context.Fail("original reason");
            expected = Assert.Throws<InvalidOperationException>(() => self.Context.Fail("different reason"));
            return ValueTask.CompletedTask;
        };
        var input = await DeliverAsync(rig);
        handler.Release.TrySetResult();
        var failure = await WaitAsync(rig.Grain.DeactivationFailure.Task);
        Assert.Same(expected, failure);
        Assert.Empty(rig.Effects);
        Assert.Empty(rig.Outbox);
        await AssertFailureReplayAsync(rig, input);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadLetter_HandlerErrorAfterFailPreservesTerminalOutcomeUnlessStorageFails(bool failWrite)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var handlerError = new IOException("after dead letter");
        handler.Body = (self, _) =>
        {
            self.Context.Fail("permanent input failure");
            throw handlerError;
        };
        var input = await DeliverAsync(rig);
        using var storage = Fixture.Storage.BlockWrite(rig.Journal);
        var finished = FinishedAsync(rig);
        handler.Release.TrySetResult();
        await storage.WaitUntilEnteredAsync();
        AssertCompletedState(rig, input);
        Assert.Equal("permanent input failure", Assert.Single(rig.Grain.GetSnapshotForTest().InboxDeadLetters).Reason);
        var storageError = new IOException("actual persistence failure");
        if (failWrite) storage.Fail(storageError);
        else storage.Release();
        await WaitAsync(finished);
        if (failWrite)
        {
            Assert.Same(storageError, await WaitAsync(rig.Grain.DeactivationFailure.Task));
            await AssertFailureReplayAsync(rig, input);
            Assert.NotEqual("permanent input failure", Assert.Single((await rig.Receiver.GetSnapshotAsync()).InboxDeadLetters).Reason);
        }
        else
        {
            Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, input)).Status);
            Assert.Empty(rig.Effects);
            Assert.Empty(rig.Outbox);
            await AssertHealthyAsync(rig);
        }
    }

    [Fact]
    public async Task Complete_StagesBusinessOutputAndDedupeBeforeHandlerReturn()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Body = (self, token) =>
        {
            self.Mutate();
            rig.Outbox.Send(self.Output);
            self.Context.Complete();
            AssertCompletedState(rig, self.Context.Message);
            Assert.Equal(1, Assert.Single(rig.Effects).Value.Count);
            Assert.Single(rig.Outbox);
            return ValueTask.CompletedTask;
        };
        var input = await DeliverAsync(rig);
        using var storage = Fixture.Storage.BlockAcknowledgement(rig.Journal);
        var finished = FinishedAsync(rig);
        handler.Release.TrySetResult();
        await storage.WaitUntilEnteredAsync();
        Assert.False(finished.IsCompleted);
        storage.Release();
        await WaitAsync(finished);
        AssertSuccess(rig, input, outputCount: 1);
        await DeactivateAsync(rig);
        var recovered = await rig.Receiver.GetSnapshotAsync();
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(1, recovered.ProcessedMessageCount);
        Assert.Equal(1, recovered.OutboxCount);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, input)).Status);
    }

    [Fact]
    public async Task Complete_PrecedingWriterCapturesCompletionWithBusinessAcrossReturnContinuation()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var input = await DeliverAsync(rig);
        var writes = Writes(rig);
        using var storage = Fixture.Storage.BlockWrite(rig.Journal);
        var preceding = OnTurnAsync(rig.Context, async () =>
        {
            rig.Context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "prior";
            await rig.Manager.WriteStateAsync(CancellationToken.None);
        });
        await storage.WaitUntilEnteredAsync();
        var priorCapture = rig.Grain.Captures[^1];
        Assert.Empty(priorCapture.Effects);
        Assert.Equal(1, priorCapture.InboxCount);
        Assert.Equal(0, priorCapture.ProcessedMessageCount);
        Assert.Equal(0, priorCapture.OutboxCount);
        var queued = OnTurnAsync(rig.Context, async () => await rig.Manager.WriteStateAsync(CancellationToken.None));
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returnContinuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Body = async (self, _) =>
        {
            self.Mutate();
            rig.Outbox.Send(self.Output);
            self.Context.Complete();
            AssertCompletedState(rig, input);
            staged.TrySetResult();
            await returnContinuation.Task;
        };
        var finished = FinishedAsync(rig);
        handler.Release.TrySetResult();
        try
        {
            await WaitAsync(staged.Task);
            Assert.False(finished.IsCompleted);
            storage.Release();
            await WaitAsync(Task.WhenAll(preceding, queued));
            Assert.False(finished.IsCompleted);
            Assert.Equal(writes + 2, Writes(rig));
            var capturedEffects = Assert.Single(rig.Grain.Captures, snapshot => snapshot.Effects.Count != 0);
            Assert.Equal(0, capturedEffects.InboxCount);
            Assert.Equal(1, capturedEffects.ProcessedMessageCount);
            Assert.Equal(1, capturedEffects.OutboxCount);
            Assert.Equal(new MessageEffect(input.Envelope.MessageId, 1, 501, "async-handler"), Assert.Single(capturedEffects.Effects));
        }
        finally
        {
            returnContinuation.TrySetResult();
        }
        await WaitAsync(finished);
        AssertSuccess(rig, input, outputCount: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandlerFailure_BeforeVsAfterCompleteUsesActualLogicalOutcome(bool completed)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var error = new IOException("Handler outcome.");
        handler.Body = (self, _) =>
        {
            if (completed)
            {
                self.Mutate();
                rig.Outbox.Send(self.Output);
                self.Context.Complete();
            }
            throw error;
        };
        var input = await DeliverAsync(rig);
        var finished = FinishedAsync(rig);
        handler.Release.TrySetResult();
        await WaitAsync(finished);
        if (completed)
        {
            AssertSuccess(rig, input, outputCount: 1);
            Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, input)).Status);
        }
        else
        {
            Assert.Empty(rig.Effects);
            Assert.Empty(rig.Outbox);
            Assert.Equal(input.Envelope.MessageId, Assert.Single(rig.Grain.GetSnapshotForTest().InboxDeadLetters).MessageId);
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
        var input = await DeliverAsync(rig);
        var writes = Writes(rig);
        handler.Release.TrySetResult();
        var error = Assert.IsType<InvalidOperationException>(await WaitAsync(rig.Grain.DeactivationFailure.Task));
        Assert.Contains("must call Complete", error.Message, StringComparison.Ordinal);
        Assert.Equal(writes, Writes(rig));
        await rig.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(input.Envelope.MessageId, Assert.Single(rig.Inbox).Key);
        Assert.Equal(input.Envelope.Payload.ToArray(), Assert.Single(rig.Inbox).Value.Message.Envelope.Payload.ToArray());
        Assert.Empty(rig.Processed);
        Assert.Empty(rig.Effects);
        await AssertFailureReplayAsync(rig, input);
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
        var input = await DeliverAsync(rig);
        var finished = FinishedAsync(rig);
        handler.Release.TrySetResult();
        await WaitAsync(finished);
        AssertSuccess(rig, input, outputCount: 0);
        await AssertHealthyAsync(rig);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("dead-letter")]
    public async Task RetainedContext_AfterRetirementRejectsAndKeepsOwnerHealthy(string operation)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var input = await DeliverAsync(rig);
        var finished = FinishedAsync(rig);
        handler.Release.TrySetResult();
        await WaitAsync(finished);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OnTurnAsync(rig.Context, () => InvokeContextOperation(handler, operation)));
        Assert.Contains("inactive or different attempt", error.Message, StringComparison.Ordinal);
        AssertSuccess(rig, input, outputCount: 0);
        await AssertHealthyAsync(rig);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("dead-letter")]
    public async Task RetainedContext_DuringOtherAttemptThrowsWithoutAffectingCurrentAttempt(string operation)
    {
        var rig = await CreateAsync();
        using var first = rig.Handler;
        var one = await DeliverAsync(rig);
        var finished = FinishedAsync(rig);
        first.Release.TrySetResult();
        await WaitAsync(finished);
        using var second = new TestHandler(rig.Effects, rig.Context);
        InvalidOperationException? rejection = null;
        second.Body = (_, _) =>
        {
            try { InvokeContextOperation(first, operation); }
            catch (InvalidOperationException error) { rejection = error; }
            throw new IOException("Replacement failure.");
        };
        await OnTurnAsync(rig.Context, () =>
            rig.Grain.HandlerOverride = second);
        var two = CreateEnvelope(rig.Receiver, NewMessage(502, "next"), "async/next");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, two)).Status);
        await WaitAsync(second.Entered.Task);
        second.Release.TrySetResult();
        var snapshot = await Fixture.WaitForDeadLetterCountAsync(rig.Receiver, 1);
        Assert.Contains("inactive or different attempt", Assert.IsType<InvalidOperationException>(rejection).Message, StringComparison.Ordinal);
        var letter = Assert.Single(snapshot.InboxDeadLetters);
        Assert.Equal(two.Envelope.MessageId, letter.MessageId);
        Assert.Equal("Replacement failure.", letter.Reason);
        Assert.Equal(one.Envelope.MessageId, Assert.Single(rig.Effects).Key);
        await AssertHealthyAsync(rig);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionWrite_StorageOutcomeDecidesCommit(bool failWrite)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Body = (self, _) =>
        {
            self.Mutate();
            rig.Outbox.Send(self.Output);
            self.Context.Complete();
            return ValueTask.CompletedTask;
        };
        var input = await DeliverAsync(rig);
        using var storage = Fixture.Storage.BlockWrite(rig.Journal);
        var finished = FinishedAsync(rig);
        handler.Release.TrySetResult();
        await storage.WaitUntilEnteredAsync();
        AssertCompletedState(rig, input);
        Assert.False(finished.IsCompleted);
        var failure = new OperationCanceledException("Actual storage canceled.", new CancellationToken(canceled: true));
        if (failWrite) storage.Fail(failure);
        else storage.Release();
        await WaitAsync(finished);
        if (failWrite)
        {
            Assert.Same(failure, await WaitAsync(rig.Grain.DeactivationFailure.Task));
            await AssertFailureReplayAsync(rig, input);
        }
        else
        {
            AssertSuccess(rig, input, outputCount: 1);
            await AssertHealthyAsync(rig);
        }
    }

    [Fact]
    public async Task CompletionWrite_PreparationFailureReplaysCompletion()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var input = await DeliverAsync(rig);
        var error = new IOException("Journal preparation failed.");
        await OnTurnAsync(rig.Context, () => rig.Grain.JournalPreparationFailure = error);
        var finished = FinishedAsync(rig);
        handler.Release.TrySetResult();
        await WaitAsync(finished);
        var failure = Assert.IsType<JournaledStatePreCommitException>(await WaitAsync(rig.Grain.DeactivationFailure.Task));
        Assert.Same(error, failure.InnerException);
        await AssertFailureReplayAsync(rig, input);
    }

    [Fact]
    public async Task PostCompleteHandlerFailure_WithStorageFailureKeepsStorageCauseAuthoritative()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var handlerError = new InvalidOperationException("After Complete.");
        handler.Body = (self, _) => { self.Mutate(); self.Context.Complete(); throw handlerError; };
        var input = await DeliverAsync(rig);
        using var storage = Fixture.Storage.BlockWrite(rig.Journal);
        var finished = FinishedAsync(rig);
        handler.Release.TrySetResult();
        await storage.WaitUntilEnteredAsync();
        var storageError = new IOException("Storage failed.");
        storage.Fail(storageError);
        await WaitAsync(finished);
        Assert.Same(storageError, await WaitAsync(rig.Grain.DeactivationFailure.Task));
        await AssertFailureReplayAsync(rig, input);
    }

    private async Task<Rig> CreateAsync()
    {
        var receiver = NewGrain();
        await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var services = context.ActivationServices;
        var effects = services.GetRequiredKeyedService<IDurableDictionary<MessageId, MessageEffect>>("test-effects");
        var handler = new TestHandler(effects, context);
        var rig = new Rig(receiver, context, Assert.IsType<MessagingTestGrain>(context.GrainInstance),
            services.GetRequiredService<IJournaledStateManager>(), (JournaledTestOutbox)services.GetRequiredService<IOutbox>(),
            effects, handler, services.GetRequiredService<Inbox>());
        await OnTurnAsync(context, () => rig.Grain.HandlerOverride = handler);
        return rig;
    }

    private async Task<InboxMessage> DeliverAsync(Rig rig)
    {
        var input = CreateEnvelope(rig.Receiver, NewMessage(501, "async-handler"), "async/handler");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, input)).Status);
        await WaitAsync(rig.Handler.Entered.Task);
        return input;
    }

    private static void InvokeContextOperation(TestHandler handler, string operation)
    {
        switch (operation)
        {
            case "complete": handler.Context.Complete(); break;
            case "dead-letter": handler.Context.Fail("unusable input"); break;
            default: throw new ArgumentException(nameof(operation));
        }
    }

    private static void AssertCompletedState(Rig rig, InboxMessage input)
    {
        Assert.Empty(rig.Inbox);
        Assert.Equal(input.Envelope.MessageId, Assert.Single(rig.Processed).Key);
    }

    private static void AssertSuccess(Rig rig, InboxMessage input, int outputCount)
    {
        AssertCompletedState(rig, input);
        Assert.Equal(new MessageEffect(input.Envelope.MessageId, 1, 501, "async-handler"), Assert.Single(rig.Effects).Value);
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

    private async Task AssertFailureReplayAsync(Rig rig, InboxMessage input)
    {
        await WaitAsync(rig.Context.Deactivated);
        await rig.Receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForDeadLetterCountAsync(rig.Receiver, 1);
        Assert.Empty(recovered.Effects);
        Assert.Equal(0, recovered.OutboxCount);
        Assert.Equal(1, recovered.ProcessedMessageCount);
        Assert.Equal(input.Envelope.MessageId, Assert.Single(recovered.InboxDeadLetters).MessageId);
    }

    /// <summary>
    /// Completes after the pump finishes its current pass (including the batch write) and one more pass.
    /// </summary>
    private Task FinishedAsync(Rig rig)
    {
        // Resolve the pump now: a failed write deactivates the grain and disposes its activation services.
        var shutdown = rig.Context.ActivationServices.GetRequiredService<MessagingPump>().ShutdownToken;
        return RunAsync();

        async Task RunAsync()
        {
            try
            {
                await Fixture.RunPumpAsync(rig.Receiver);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                // A failed write stops the pump; callers assert the failure explicitly.
            }
        }
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
            catch (Exception error)
            {
                done.SetException(error);
            }
        }
    }

    private static Task WaitAsync(Task task) => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    private static Task<T> WaitAsync<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    private sealed record Rig(IMessagingTestGrain Receiver, IGrainContext Context, MessagingTestGrain Grain,
        IJournaledStateManager Manager, JournaledTestOutbox Outbox, IDurableDictionary<MessageId, MessageEffect> Effects,
        TestHandler Handler, Inbox InboxState)
    {
        public IReadOnlyDictionary<MessageId, InboxEntry> Inbox => InboxTestAccess.GetPendingMessages(InboxState);
        public IReadOnlyDictionary<MessageId, DateTimeOffset> Processed => InboxTestAccess.GetProcessedMessages(InboxState);

        public JournalId Journal => JournalId.FromGrainId(Receiver.GetGrainId());
    }

    private sealed class TestHandler(IDurableDictionary<MessageId, MessageEffect> effects, IGrainContext grainContext) : IInboxHandler, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IInboxHandlerContext Context { get; private set; } = null!;
        public OutboxMessage Output { get; private set; }
        public Func<TestHandler, CancellationToken, ValueTask> Body { get; set; } =
            static (self, _) => { self.Mutate(); self.Context.Complete(); return default; };
        public bool CanHandle(IInboxHandlerContext context) => true;

        public async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            Context = context;

            Output = TestApplicationProtocol.Create(grainContext.ActivationServices.GetRequiredService<SerializerSessionPool>(), grainContext.GrainId, grainContext.GrainId, "output", 41);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await Body(this, cancellationToken);
        }
        public void Mutate()
        {
            effects.TryGetValue(Context.Message.Envelope.MessageId, out var previous);
            effects[Context.Message.Envelope.MessageId] =
                new MessageEffect(Context.Message.Envelope.MessageId, (previous?.Count ?? 0) + 1, 501, "async-handler");
        }
        public void Dispose()
        {
            Release.TrySetResult();

        }
    }
}
