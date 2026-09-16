using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging.Tests.Support;
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
