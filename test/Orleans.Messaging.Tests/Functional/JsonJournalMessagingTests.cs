using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class JsonJournalMessagingTests(JsonJournalMessagingTests.Fixture fixture) : IClassFixture<JsonJournalMessagingTests.Fixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private InProcessTestCluster Cluster => fixture.Cluster;
    private JsonMessagingProbe Probe => fixture.Probe;

    [Fact]
    public async Task JsonJournal_RecoversOutboxAndInboxStateAcrossActivations()
    {
        var sender = Cluster.Client.GetGrain<IJsonMessagingGrain>(Guid.NewGuid());
        var receiver = Cluster.Client.GetGrain<IJsonMessagingGrain>(Guid.NewGuid());
        var messageId = HierarchicalKey.Create("json", Guid.NewGuid().ToString("N"));

        // The sender deactivates after committing the send, so delivery runs from the outbox recovered from the JSON journal.
        await sender.SendAndDeactivateAsync(receiver.GetGrainId(), messageId, 42);
        Assert.Equal(new[] { 42 }, await Probe.WaitAsync(receiver.GetGrainId(), 1, Token));
        Assert.True(Cluster.TryGetGrainContext(receiver.GetGrainId(), out var receiverContext));
        Assert.Contains("Json", receiverContext.GetComponent<MessagingShared>()!.InboxCodec.GetType().Name, StringComparison.Ordinal);
        Assert.Same(receiverContext.ActivationServices.GetRequiredService<Inbox>(), receiverContext.ActivationServices.GetRequiredService<IInbox>());
        while (await sender.GetOutboxCountAsync() != 0)
        {
            await Task.Delay(10, Token);
        }

        await receiver.DeactivateAsync();
        await receiverContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Token);
        var type = new MessageType<int>(JsonMessagingGrain.Subject, Cluster.Client.ServiceProvider.GetRequiredService<Serializer<int>>());
        var redelivery = type.Create(messageId, receiver.GetGrainId(), 42).ToInboxMessage();

        Assert.Equal(DeliveryStatus.Duplicate, (await receiver.AsReference<IInboxExtension>().DeliverAsync(redelivery, Token)).Status);
        Assert.Equal(new[] { 42 }, Probe.Get(receiver.GetGrainId()));
    }

    [Fact]
    public async Task CustomInboxHandlers_FirstHandlerWhichCanHandleReceivesMessage()
    {
        var receiver = Cluster.Client.GetGrain<ICustomHandlerGrain>(Guid.NewGuid());
        var message = new InboxMessage
        {
            Envelope = new Envelope { MessageId = HierarchicalKey.Create("custom", Guid.NewGuid().ToString("N")), Payload = [7], Subject = "custom" }
        };

        var result = await receiver.AsReference<IInboxExtension>().DeliverAsync(message, Token);

        Assert.Equal(DeliveryStatus.Accepted, result.Status);
        Assert.Equal(new[] { CustomHandlerGrain.FirstMatch }, await Probe.WaitAsync(receiver.GetGrainId(), 1, Token));
    }

    [Fact]
    public async Task JsonJournal_RecoversScheduledInboxMessageWithItsDeliverAt()
    {
        var receiver = Cluster.Client.GetGrain<IJsonMessagingGrain>(Guid.NewGuid());
        var messageId = HierarchicalKey.Create("json", "scheduled", Guid.NewGuid().ToString("N"));
        var type = new MessageType<int>(JsonMessagingGrain.Subject, Cluster.Client.ServiceProvider.GetRequiredService<Serializer<int>>());
        var deliverAt = new DateTimeOffset(DateTime.UtcNow.AddDays(1).Ticks, TimeSpan.Zero).ToOffset(TimeSpan.FromHours(5));
        var scheduled = type.Create(messageId, receiver.GetGrainId(), 7, deliverAt).ToInboxMessage();
        var extension = receiver.AsReference<IInboxExtension>();

        Assert.Equal(DeliveryStatus.Accepted, (await extension.DeliverAsync(scheduled, Token)).Status);
        Assert.True(Cluster.TryGetGrainContext(receiver.GetGrainId(), out var receiverContext));
        await receiver.DeactivateAsync();
        await receiverContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Token);

        // Redelivery replays the JSON journal; pending-command equivalence requires the recovered DeliverAt.
        Assert.Equal(DeliveryStatus.Duplicate, (await extension.DeliverAsync(scheduled, Token)).Status);
        var conflicting = type.Create(messageId, receiver.GetGrainId(), 7, deliverAt.AddSeconds(1)).ToInboxMessage();
        await Assert.ThrowsAsync<InvalidOperationException>(() => extension.DeliverAsync(conflicting, Token).AsTask());
        Assert.True(Cluster.TryGetGrainContext(receiver.GetGrainId(), out var recoveredContext));
        Assert.NotSame(receiverContext, recoveredContext);
        var inbox = recoveredContext.ActivationServices.GetRequiredService<Inbox>();
        var recovered = new TaskCompletionSource<DateTimeOffset?>(TaskCreationOptions.RunContinuationsAsynchronously);
        recoveredContext.Scheduler.QueueAction(() => recovered.SetResult(inbox.MessageState[messageId].Message.Envelope.DeliverAt));
        Assert.Equal(deliverAt, await recovered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token));
        Assert.Empty(Probe.Get(receiver.GetGrainId()));
    }

    public sealed class Fixture : IAsyncLifetime
    {
        public JsonMessagingProbe Probe { get; } = new();
        public InProcessTestCluster Cluster { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            var builder = new InProcessTestClusterBuilder(1);
            builder.ConfigureSilo((_, silo) =>
            {
                silo.AddVolatileJournalStorage();
                silo.Services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "json");
                silo.UseInMemoryDurableJobs();
                silo.AddMessaging();
                silo.Services.AddSingleton(Probe);
            });
            Cluster = builder.Build();
            await Cluster.DeployAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync() => await Cluster.DisposeAsync();
    }
}

public interface ICustomHandlerGrain : IGrainWithGuidKey;

public sealed class CustomHandlerGrain : Grain, ICustomHandlerGrain, IMessagingGrain
{
    internal const int Declined = 1;
    internal const int FirstMatch = 2;
    internal const int SecondMatch = 3;

    public CustomHandlerGrain(IInbox inbox, IGrainContext context, JsonMessagingProbe probe) =>
        inbox.Register(new Handler(Declined, false, context, probe))
            .Register(new Handler(FirstMatch, true, context, probe))
            .Register(new Handler(SecondMatch, true, context, probe));

    private sealed class Handler(int id, bool canHandle, IGrainContext grain, JsonMessagingProbe probe) : IInboxHandler
    {
        public bool CanHandle(IInboxHandlerContext context) => canHandle && context.Message.Envelope.Subject == "custom";

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            probe.Record(grain.GrainId, id);
            context.Complete();
            return ValueTask.CompletedTask;
        }
    }
}
public interface IJsonMessagingGrain : IGrainWithGuidKey
{
    Task SendAndDeactivateAsync(GrainId destination, HierarchicalKey messageId, int value);
    Task<int> GetOutboxCountAsync();
    Task DeactivateAsync();
}

public sealed class JsonMessagingProbe
{
    private readonly ConcurrentDictionary<GrainId, ConcurrentQueue<int>> _received = new();

    public void Record(GrainId grainId, int value) => _received.GetOrAdd(grainId, static _ => new()).Enqueue(value);

    public int[] Get(GrainId grainId) => _received.TryGetValue(grainId, out var values) ? values.ToArray() : [];

    public async Task<int[]> WaitAsync(GrainId grainId, int count, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (Get(grainId).Length < count)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"No message was handled by '{grainId}'.");
            await Task.Delay(10, cancellationToken);
        }

        return Get(grainId);
    }
}

public sealed class JsonMessagingGrain : Grain, IJsonMessagingGrain, IMessagingGrain
{
    internal const string Subject = "json/value";
    private readonly IJournaledStateManager _manager;
    private readonly IOutbox _outbox;
    private readonly MessageType<int> _type;
    private readonly JsonMessagingProbe _probe;

    public JsonMessagingGrain(IJournaledStateManager manager, IInbox inbox, IOutbox outbox, Serializer<int> serializer, JsonMessagingProbe probe)
    {
        _manager = manager;
        _outbox = outbox;
        _probe = probe;
        _type = new MessageType<int>(Subject, serializer);
        inbox.Register(_type, this, static (value, grain, context) =>
        {
            grain._probe.Record(grain.GetGrainId(), value);
            context.Complete();
        });
    }

    public async Task SendAndDeactivateAsync(GrainId destination, HierarchicalKey messageId, int value)
    {
        _outbox.Send(_type, messageId, destination, value);
        await _manager.WriteStateAsync(CancellationToken.None);
        DeactivateOnIdle();
    }

    public Task<int> GetOutboxCountAsync() => Task.FromResult(_outbox.Count());

    public Task DeactivateAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }
}
