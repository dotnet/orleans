#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Runtime;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Records staged messages in send order, including repeated sends of a pending identity.
/// </summary>
/// <remarks>
/// The messaging outbox stages a message addressed to the sending grain, such as a delay resume, directly in that grain's
/// own inbox; this fake records every send the adapter makes through <see cref="IOutbox"/>, whatever its receiver.
/// </remarks>
internal sealed class RecordingOutbox : IOutbox
{
    private readonly List<OutboxMessage> _sends = [];
    private readonly List<MessageId> _sourceSends = [];

    /// <summary>Gets every staged send, including sends which coalesce with a pending identity.</summary>
    public IReadOnlyList<OutboxMessage> Messages => _sends;

    /// <summary>Gets every staged reference send, whose message a registered source provides.</summary>
    public IReadOnlyList<MessageId> SourceMessages => _sourceSends;

    public void Send(OutboxMessage message) => _sends.Add(message);

    public void Send(MessageId messageId) => _sourceSends.Add(messageId);

    public IOutbox Register(IOutboxMessageSource source) => throw new NotSupportedException();
}

internal static class MessagingTestHelpers
{
    private static readonly ServiceProvider Services = new ServiceCollection()
        .AddSerializer(builder => builder
            .AddAssembly(typeof(DurableTaskMessageTransport).Assembly)
            .AddAssembly(typeof(MessagingTestHelpers).Assembly))
        .BuildServiceProvider();

    public static Serializer Serializer => Services.GetRequiredService<Serializer>();

    public static DurableTaskMessageTypes MessageTypes { get; } = new(Services.GetRequiredService<Serializer>());

    public static DurableTaskMessageTransport CreateTransport(RecordingOutbox outbox) =>
        new(new Lazy<IOutbox>(outbox), MessageTypes);

    public static InboxMessage CreateMessage<T>(MessageType<T> messageType, GrainId sender, GrainId receiver, TaskId taskId, T body) =>
        messageType.Create(
            DurableTaskMessageTransport.CreateMessageId(sender, receiver, taskId, messageType.Subject),
            receiver,
            body).ToInboxMessage();

    public static InboxMessage CreateResumeMessage(GrainId grainId, TaskId taskId, long generation, DateTimeOffset dueTime) =>
        MessageTypes.Resume.Create(
            DurableTaskMessageTransport.CreateResumeMessageId(grainId, taskId, generation, dueTime),
            grainId,
            new DurableTaskResumeMessage { TaskId = taskId, Generation = generation, Sender = grainId },
            dueTime).ToInboxMessage();

    public static IInboxHandlerContext CreateHandlerContext(InboxMessage message)
    {
        var context = Substitute.For<IInboxHandlerContext>();
        context.Message.Returns(message);
        return context;
    }
}
