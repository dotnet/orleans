#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Runtime;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Records staged messages in send order, coalescing equivalent sends of a pending identity like the messaging outbox.
/// </summary>
internal sealed class RecordingOutbox : IOutbox
{
    private readonly List<OutboxMessage> _sends = [];

    /// <summary>Gets every staged send, including sends which coalesce with a pending identity.</summary>
    public IReadOnlyList<OutboxMessage> Messages => _sends;

    public void Send(OutboxMessage message) => _sends.Add(message);
}

internal sealed class RecordingJobManager : ILocalDurableJobManager
{
    public List<ScheduleJobRequest> Requests { get; } = [];

    public Task<DurableJob> ScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.JobName,
            DueTime = request.DueTime,
            TargetGrainId = request.Target,
            ShardId = "test",
            Metadata = request.Metadata,
        });
    }

    public Task<bool> CancelAsync(DurableJob job, CancellationToken cancellationToken) =>
        Task.FromResult(true);
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

    public static DurableTaskMessageTransport CreateTransport(RecordingOutbox outbox, ILocalDurableJobManager? jobs = null) =>
        new(new Lazy<IOutbox>(outbox), jobs ?? new RecordingJobManager(), MessageTypes);

    public static InboxMessage CreateMessage<T>(MessageType<T> messageType, GrainId sender, GrainId receiver, TaskId taskId, T body) =>
        messageType.Create(
            DurableTaskMessageTransport.CreateMessageId(sender, receiver, taskId, messageType.Subject),
            receiver,
            body).ToInboxMessage();

    public static IInboxHandlerContext CreateHandlerContext(InboxMessage message)
    {
        var context = Substitute.For<IInboxHandlerContext>();
        context.Message.Returns(message);
        return context;
    }
}
