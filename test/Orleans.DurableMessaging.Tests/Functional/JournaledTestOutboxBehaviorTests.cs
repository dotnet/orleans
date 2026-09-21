using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class JournaledTestOutboxBehaviorTests : DurableMessagingBehaviorTestBase
{
    [Fact]
    public async Task EquivalentDuplicates_BeforeAndAfterCommit_PreserveOneOutput()
    {
        var owner = NewGrain();
        var original = CreateOutput(owner);
        var reordered = original with { Data = CreateOutput(owner, reverseContext: true).Data };
        var serializer = Fixture.Client.ServiceProvider.GetRequiredService<Serializer<DurableEnvelope>>();
        var copy = serializer.Deserialize(serializer.SerializeToArray(original));
        Assert.NotSame(original.Data, reordered.Data);
        Assert.NotSame(original.Data, copy.Data);

        await owner.StageOutputAsync(original);
        var retained = Assert.Single(Fixture.GetStagedOutput(owner));
        await owner.StageOutputAsync(reordered);
        Assert.Same(retained.Data, Assert.Single(Fixture.GetStagedOutput(owner)).Data);
        await owner.RetryWriteStateAsync();
        await owner.StageOutputAsync(copy);
        Assert.Same(retained.Data, Assert.Single(Fixture.GetStagedOutput(owner)).Data);
        var before = await owner.GetSnapshotAsync();
        await owner.RequestDeactivationAsync();
        var recovered = await owner.GetSnapshotAsync();
        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        await owner.StageOutputAsync(copy);

        var output = Assert.Single(Fixture.GetStagedOutput(owner));
        Assert.Equal(original.MessageId, output.MessageId);
        Assert.True(output.Data.TryGetBody<int>(out var body));
        Assert.Equal(0, body);
        Assert.True(output.Data.TryGetContextValue<int>("attempt", out var attempt));
        Assert.Equal(0, attempt);
        Assert.True(output.Data.TryGetContextValue<string>("tenant", out var tenant));
        Assert.Equal("northwind", tenant);
        Assert.Equal(1, recovered.OutboxCount);
    }

    [Theory]
    [InlineData("sender")]
    [InlineData("receiver")]
    [InlineData("route")]
    [InlineData("correlation")]
    [InlineData("reply-to")]
    [InlineData("created-at")]
    [InlineData("body-bytes")]
    [InlineData("body-type")]
    [InlineData("context-bytes")]
    [InlineData("context-type")]
    [InlineData("context-key")]
    public async Task ConflictingDuplicate_RejectsChangedEnvelopeAndPreservesCommittedOutput(string difference)
    {
        var owner = NewGrain();
        var original = CreateOutput(owner);
        await owner.StageOutputAsync(original);
        await owner.RetryWriteStateAsync();
        var retained = Assert.Single(Fixture.GetStagedOutput(owner));
        var conflicting = difference switch
        {
            "sender" => original with { SenderId = GrainId.Create("other-sender", "1") },
            "receiver" => original with { ReceiverId = GrainId.Create("other-receiver", "1") },
            "route" => original with { RouteKey = "output/Route" },
            "correlation" => original with { CorrelationKey = HierarchicalKey.Create("other/child") },
            "reply-to" => original with { ReplyTo = GrainId.Create("other-reply", "1") },
            "created-at" => original with { CreatedAt = original.CreatedAt.AddTicks(1) },
            _ => original with { Data = CreateOutput(owner, difference).Data }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => owner.StageOutputAsync(conflicting));

        Assert.Contains(original.MessageId.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Same(retained.Data, Assert.Single(Fixture.GetStagedOutput(owner)).Data);
        await owner.RetryWriteStateAsync();
        await owner.RequestDeactivationAsync();
        var recovered = await owner.GetSnapshotAsync();
        Assert.Equal(1, recovered.OutboxCount);
        var output = Assert.Single(Fixture.GetStagedOutput(owner));
        Assert.Equal(original.MessageId, output.MessageId);
        Assert.Equal(original.SenderId, output.SenderId);
        Assert.Equal(original.ReceiverId, output.ReceiverId);
        Assert.Equal(original.RouteKey, output.RouteKey);
        Assert.Equal(original.CorrelationKey, output.CorrelationKey);
        Assert.Equal(original.ReplyTo, output.ReplyTo);
        Assert.Equal(original.CreatedAt, output.CreatedAt);
        Assert.True(output.Data.TryGetBody<int>(out var body));
        Assert.Equal(0, body);
        Assert.True(output.Data.TryGetContextValue<int>("attempt", out var attempt));
        Assert.Equal(0, attempt);
    }

    [Fact]
    public async Task HandlerEquivalentDuplicateOutput_CommitsOneEffectAndOneOutput()
    {
        var receiver = NewGrain();
        var target = GrainId.Create("output-target", "handler");
        using var envelope = CreateEnvelope(
            receiver,
            NewMessage(97, "duplicate-output") with { ForwardTo = target },
            "messages/duplicate-output");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var completed = await Fixture.SnapshotProbe.WaitAsync(receiver.GetGrainId(),
            static snapshot => snapshot.InboxCount == 0 && (snapshot.Effects.Count > 0 || snapshot.InboxDeadLetters.Count > 0));

        Assert.Empty(completed.InboxDeadLetters);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Equal(1, completed.OutboxCount);
        var output = Assert.Single(Fixture.GetStagedOutput(receiver));
        Assert.Equal(target, output.ReceiverId);
        await receiver.RequestDeactivationAsync();
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(completed.ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(output.MessageId, Assert.Single(Fixture.GetStagedOutput(receiver)).MessageId);
        Assert.Empty(recovered.InboxDeadLetters);
    }

    [Fact]
    public async Task ApplicationPreparation_CopiesBatchAndStagesOnlyOnSendBeforeExplicitWrite()
    {
        var owner = NewGrain();
        _ = await owner.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(owner);
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var journal = JournalId.FromGrainId(owner.GetGrainId());
        var first = CreateOutput(owner);
        var second = CreateOutput(owner);
        var replaced = CreateOutput(owner);
        var messages = new[] { first, second };
        using var preparation = outbox.BlockNextPreparation();
        var acquisition = OnTurnAsync(context, () => outbox.PrepareSendAsync(messages, TestContext.Current.CancellationToken));
        await preparation.WaitAsync();
        messages[0] = replaced;
        Assert.Empty(outbox.Messages);
        Assert.Equal(0, ((IDurableOutbox)outbox).Count);
        Assert.Empty(outbox.PreparedBatches);
        await OnTurnAsync(context, () =>
            context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "prior-only");
        await Fixture.WriteStateAsync(owner);
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        Assert.Empty(grain.OutputCaptures[^1]);
        Assert.Equal(0, grain.Captures[^1].OutboxCount);

        preparation.Release();
        using (var batch = await acquisition)
        {
            Assert.Empty(outbox.Messages);
            Assert.Equal(0, ((IDurableOutbox)outbox).Count);
            Assert.Equal(new[] { first.MessageId, second.MessageId }, Assert.Single(outbox.PreparedBatches).MessageIds);
            await Fixture.WriteStateAsync(owner);
            Assert.Empty(grain.OutputCaptures[^1]);
            Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
            await OnTurnAsync(context, () =>
            {
                outbox.Send(batch);
                outbox.Send(batch);
            });
            Assert.Equal(2, outbox.Count);
            Assert.Equal(new[] { first.MessageId, second.MessageId }.Order(), outbox.Messages.Select(static item => item.MessageId).Order());
            Assert.DoesNotContain(outbox.Messages, item => item.MessageId == replaced.MessageId);
            Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        }

        Assert.Equal(1, Assert.Single(outbox.PreparedBatches).DisposeCalls);
        await owner.RetryWriteStateAsync();
        Assert.Equal(writes + 1, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(new[] { first.MessageId, second.MessageId }.Order(), grain.OutputCaptures[^1].Order());
        await owner.RequestDeactivationAsync();
        Assert.Equal(2, (await owner.GetSnapshotAsync()).OutboxCount);
        Assert.Equal(new[] { first.MessageId, second.MessageId }.Order(), Fixture.GetStagedOutput(owner).Select(static item => item.MessageId).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplicationPreparationFailure_BeforeMutation_KeepsManagerHealthy(bool priorAcquisition)
    {
        var owner = NewGrain();
        _ = await owner.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(owner);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
        var output = CreateOutput(owner);
        var journal = JournalId.FromGrainId(owner.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        IPreparedOutboxBatch? unused = priorAcquisition
            ? await OnTurnAsync(context, () => outbox.PrepareSendAsync([CreateOutput(owner)]))
            : null;
        using var preparation = outbox.BlockNextPreparation();
        var acquisition = OnTurnAsync(context, () => outbox.PrepareSendAsync([output]));
        await preparation.WaitAsync();
        var failure = new IOException("Ordinary application preparation failed before mutation.");
        preparation.Fail(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => acquisition));
        unused?.Dispose();
        Assert.Empty(outbox.Messages);
        Assert.Empty(grain.GetSnapshotForTest().Effects);
        Assert.Equal(0, grain.GetSnapshotForTest().ProcessedMessageCount);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.False(grain.DeactivationFailure.Task.IsCompleted);
        Assert.All(outbox.PreparedBatches, static batch => Assert.Equal(1, batch.DisposeCalls));

        await OnTurnAsync(context, () =>
            context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "healthy-after-preparation-failure");
        await Fixture.WriteStateAsync(owner);
        Assert.Same(context, Fixture.GetGrainContext(owner));
        Assert.False(grain.DeactivationFailure.Task.IsCompleted);
        await owner.StageOutputAsync(output);
        await owner.RetryWriteStateAsync();
        Assert.Equal(output.MessageId, Assert.Single(outbox.Messages).MessageId);
        Assert.Equal(writes + 2, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.All(outbox.PreparedBatches, static batch => Assert.Equal(1, batch.DisposeCalls));
    }

    [Fact]
    public async Task EmptyPreparedBatch_SendDoesNotStageOutputOrChangeReceiverState()
    {
        var owner = NewGrain();
        var before = await owner.GetSnapshotAsync();
        // Persist initial state-directory enrollment before measuring an empty batch's work.
        await owner.RetryWriteStateAsync();
        var context = Fixture.GetGrainContext(owner);
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
        var journal = JournalId.FromGrainId(owner.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        using (var batch = await OnTurnAsync(context, () => outbox.PrepareSendAsync([])))
        {
            await OnTurnAsync(context, () => { outbox.Send(batch); outbox.Send(batch); });
            Assert.Empty(outbox.Messages);
            Assert.Empty(Assert.Single(outbox.PreparedBatches).MessageIds);
            Assert.True(Assert.Single(outbox.PreparedBatches).IsStaged);
        }
        await owner.RetryWriteStateAsync();
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        var after = await owner.GetSnapshotAsync();
        Assert.Equal(before.ActivationId, after.ActivationId);
        Assert.Equal(0, after.InboxCount);
        Assert.Equal(0, after.OutboxCount);
        Assert.Equal(0, after.ProcessedMessageCount);
        Assert.Empty(after.Effects);
        Assert.Empty(after.InboxDeadLetters);
        Assert.Null(after.InboxJobId);
        Assert.Equal(1, Assert.Single(outbox.PreparedBatches).DisposeCalls);
        // Controlled collaborator only: production outbox wakeup ownership is tested downstream.
        Assert.Equal(0, Fixture.JobManagerProbe.GetAttemptCount("orleans.messaging.outbox-drain", owner.GetGrainId()));
    }

    [Fact]
    public async Task PreparedBatch_ConflictIntroducedAfterPreparation_DoesNotStagePartialPrefix()
    {
        var owner = NewGrain();
        _ = await owner.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(owner);
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
        var first = CreateOutput(owner);
        var original = CreateOutput(owner);
        var conflict = original with { RouteKey = "other-output" };
        using var prepared = await OnTurnAsync(context, () => outbox.PrepareSendAsync([first, conflict]));
        await owner.StageOutputAsync(original);
        await owner.RetryWriteStateAsync();
        var retained = Assert.Single(outbox.Messages);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => OnTurnAsync(context, () => outbox.Send(prepared)));
        Assert.Contains(original.MessageId.ToString(), failure.Message, StringComparison.Ordinal);
        Assert.Equal(original.MessageId, Assert.Single(outbox.Messages).MessageId);
        Assert.Same(retained.Data, Assert.Single(outbox.Messages).Data);
        Assert.False(outbox.TryGetMessage(first.MessageId, out _));
        Assert.False(outbox.PreparedBatches[0].IsStaged);
        await owner.RetryWriteStateAsync();
        await owner.RequestDeactivationAsync();
        Assert.Equal(1, (await owner.GetSnapshotAsync()).OutboxCount);
        Assert.Equal(original.RouteKey, Assert.Single(Fixture.GetStagedOutput(owner)).RouteKey);
    }

    private static Task<T> OnTurnAsync<T>(IGrainContext context, Func<ValueTask<T>> action)
    {
        var started = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try { started.SetResult(action().AsTask()); }
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

    private DurableEnvelope CreateOutput(IDurableMessagingTestGrain owner, string difference = "none", bool reverseContext = false)
    {
        var builder = new DurableEnvelopeBuilder(
                Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>(), owner.GetGrainId())
            .To(GrainId.Create("output-target", "1"), "output/route")
            .WithReplyTo(GrainId.Create("output-reply", "1"))
            .WithCorrelationKey("root/child");
        if (difference == "body-type")
        {
            builder.WithBody(0U);
        }
        else
        {
            builder.WithBody(difference == "body-bytes" ? 1 : 0);
        }

        if (reverseContext)
        {
            builder.WithContextValue("tenant", "northwind");
        }

        if (difference == "context-type")
        {
            builder.WithContextValue("attempt", 0U);
        }
        else
        {
            builder.WithContextValue(difference == "context-key" ? "other" : "attempt", difference == "context-bytes" ? 1 : 0);
        }

        if (!reverseContext)
        {
            builder.WithContextValue("tenant", "northwind");
        }

        return builder.Build();
    }
}
