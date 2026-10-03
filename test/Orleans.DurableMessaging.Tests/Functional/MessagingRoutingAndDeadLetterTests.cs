using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class MessagingRoutingAndDeadLetterTests : DurableMessagingBehaviorTestBase
{
    [Fact]
    public async Task InboxDeadLettersRetainNewestEntriesWithinConfiguredCapacity()
    {
        var receiver = NewGrain();
        var messageIds = new List<Guid>();
        for (var sequence = 0; sequence < 3; sequence++)
        {
            using var envelope = CreateEnvelope(
                receiver,
                new DurableTestMessage(
                    Guid.NewGuid(),
                    20 + sequence,
                    $"dead-letter-{sequence}",
                    ThrowDuringPreparation: true));
            messageIds.Add(envelope.Value.MessageId);
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
            await Fixture.SnapshotProbe.WaitAsync(
                receiver.GetGrainId(),
                snapshot => snapshot.InboxDeadLetters.Any(entry => entry.MessageId == envelope.Value.MessageId));
        }

        var state = await receiver.GetSnapshotAsync();
        Assert.Equal(2, state.InboxDeadLetters.Count);
        Assert.DoesNotContain(state.InboxDeadLetters, entry => entry.MessageId == messageIds[0]);
        Assert.Contains(state.InboxDeadLetters, entry => entry.MessageId == messageIds[1]);
        Assert.Contains(state.InboxDeadLetters, entry => entry.MessageId == messageIds[2]);
    }

    [Fact]
    public async Task ActivationRemovesExpiredInboxDeadLetters()
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(
            receiver,
            new DurableTestMessage(
                Guid.NewGuid(),
                30,
                "expired-dead-letter",
                ThrowDuringPreparation: true));

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var before = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Fixture.Clock.Advance(TimeSpan.FromHours(2));
        await receiver.RequestDeactivationAsync();
        var after = await Fixture.SnapshotProbe.WaitAsync(
            receiver.GetGrainId(),
            snapshot => snapshot.ActivationId != before.ActivationId
                && snapshot.InboxDeadLetters.Count == 0);

        Assert.Empty(after.InboxDeadLetters);
    }

    [Fact]
    public async Task MalformedTypedBody_DeadLettersAndDoesNotBlockLaterValidMessage()
    {
        var receiver = NewGrain();
        using var malformed = CreateEnvelope(receiver, "wrong-body", "typed");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, malformed.Value)).Status);
        var poisoned = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);
        Assert.Empty(poisoned.Effects);
        var deadLetter = Assert.Single(poisoned.InboxDeadLetters);
        Assert.Equal(malformed.Value.MessageId, deadLetter.MessageId);
        Assert.Contains(nameof(DurableTestMessage), deadLetter.Reason, StringComparison.Ordinal);

        using var valid = CreateEnvelope(receiver, NewMessage(41, "valid-after-poison"), "typed");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, valid.Value)).Status);
        var recovered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("valid-after-poison", Assert.Single(recovered.Effects).Value);
        Assert.Single(recovered.InboxDeadLetters);
    }

    [Fact]
    public async Task InboxDeadLetterRemoval_IsDurable()
    {
        var receiver = NewGrain();
        using var malformed = CreateEnvelope(receiver, "wrong-body", "typed");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, malformed.Value)).Status);
        _ = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Assert.True(await receiver.RemoveInboxDeadLetterAsync(
            malformed.Value.SenderId,
            malformed.Value.MessageId));
        Assert.Empty((await receiver.GetSnapshotAsync()).InboxDeadLetters);
        Assert.False(await receiver.RemoveInboxDeadLetterAsync(
            malformed.Value.SenderId,
            malformed.Value.MessageId));

        await receiver.RequestDeactivationAsync();
        Assert.Empty((await receiver.GetSnapshotAsync()).InboxDeadLetters);
    }

    [Fact]
    public async Task DuplicateExactRouteRegistration_ThrowsAndPreservesLookupAndDispatch()
    {
        var receiver = NewGrain();
        const string route = "exact/duplicate";

        var registration = await receiver.RegisterDuplicateExactRouteHandlersAsync(route);

        Assert.Equal(
            "A handler is already registered for exact route 'exact/duplicate'.",
            registration.ExceptionMessage);
        Assert.True(registration.LookupRetainedFirstHandler);

        using var envelope = CreateEnvelope(receiver, NewMessage(69, "first-handler"), route);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var state = await Fixture.SnapshotProbe.WaitAsync(
            receiver.GetGrainId(),
            static snapshot => snapshot.FirstExactRouteHandlerCalls == 1);
        Assert.Equal(1, state.FirstExactRouteHandlerCalls);
        Assert.Equal(0, state.ReplacementExactRouteHandlerCalls);
        Assert.Equal(0, state.GenericExactRouteHandlerCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task RouteLookup_RejectsInvalidRouteKeys(string? route)
    {
        var result = await NewGrain().ValidateRouteLookupAsync(route);

        Assert.Equal("routeKey", result.HasHandlerParameterName);
        Assert.Equal("routeKey", result.TryGetHandlerParameterName);
    }

    [Fact]
    public async Task RouteNotFound_IsRejectedWithoutInboxPersistence()
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(receiver, NewMessage(71, "missing"), "unknown/route");

        var result = await DeliverAsync(receiver, envelope.Value);

        Assert.Equal(DeliveryStatus.RouteNotFound, result.Status);
        Assert.Equal("No handler for route 'unknown/route'", result.Message);
        var state = await receiver.GetSnapshotAsync();
        Assert.Equal(0, state.InboxCount);
        Assert.Empty(state.Effects);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Deliver_InvalidEnvelopeRoute_IsRejectedWithoutHandlerSelection(string? routeKey)
    {
        var receiver = NewGrain();
        using var template = CreateEnvelope(receiver, NewMessage(72, "invalid-route"));
        var envelope = new DurableEnvelope
        {
            MessageId = template.Value.MessageId,
            SenderId = template.Value.SenderId,
            ReceiverId = template.Value.ReceiverId,
            RouteKey = routeKey!,
            CorrelationKey = template.Value.CorrelationKey,
            ReplyTo = template.Value.ReplyTo,
            Data = template.Value.Data,
            CreatedAt = template.Value.CreatedAt
        };

        var result = await DeliverAsync(receiver, envelope);

        Assert.Equal(DeliveryStatus.RouteNotFound, result.Status);
        var state = await receiver.GetSnapshotAsync();
        Assert.Equal(0, state.InboxCount);
        Assert.Empty(state.Effects);
    }

    [Fact]
    public async Task Deliver_RejectsEnvelopeAddressedToAnotherGrain()
    {
        var receiver = NewGrain();
        var declaredReceiver = NewGrain();
        using var envelope = CreateEnvelope(
            declaredReceiver,
            NewMessage(74, "wrong-receiver"));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => DeliverAsync(receiver, envelope.Value));

        Assert.Contains(declaredReceiver.GetGrainId().ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains(receiver.GetGrainId().ToString(), exception.Message, StringComparison.Ordinal);
        var state = await receiver.GetSnapshotAsync();
        Assert.Equal(0, state.InboxCount);
        Assert.Empty(state.Effects);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(declaredReceiver, envelope.Value)).Status);
        var delivered = await Fixture.WaitForEffectCountAsync(declaredReceiver, 1);
        Assert.Single(delivered.Effects);
    }

}
