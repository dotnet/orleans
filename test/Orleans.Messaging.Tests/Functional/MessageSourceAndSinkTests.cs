using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

/// <summary>
/// Outbox sources and inbox sinks keep application-owned payloads as the only journaled copy.
/// </summary>
public abstract class MessageSourceAndSinkTests(MessageSourceAndSinkFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private InProcessTestCluster Cluster => fixture.Cluster;
    private RecordingJournalStorageProvider Storage => fixture.Storage;
    private SourceSinkProbe Probe => fixture.Probe;

    [Fact]
    public async Task SinkAcceptance_DeduplicatesWithinTheWindowAndIsOfferedAgainAfterItExpires()
    {
        var receiver = NewGrain();
        var message = NewInboxMessage("deduplicated", NewPayload());
        var extension = receiver.AsReference<IInboxExtension>();

        Assert.Equal(DeliveryStatus.Accepted, (await extension.DeliverAsync(message, Token)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await extension.DeliverAsync(message, Token)).Status);
        Assert.Equal(1, Probe.For(receiver).SinkAcceptances);
        Assert.Equal(0, await OnGrainAsync(receiver, context => Inbox(context).Count));

        fixture.Clock.Advance(MessageSourceAndSinkFixture.DeduplicationWindow);
        Assert.Equal(DeliveryStatus.Accepted, (await extension.DeliverAsync(message, Token)).Status);
        Assert.Equal(2, Probe.For(receiver).SinkAcceptances);
        Assert.Equal(0, Probe.For(receiver).Handled);
        Assert.Equal(0, await OnGrainAsync(receiver, context => Inbox(context).Count));
    }

    [Fact]
    public async Task SinkException_DeadLettersTheMessageAndRecordsItAsProcessed()
    {
        var receiver = NewGrain();
        Probe.For(receiver).Mode = SinkMode.Throw;
        var payload = NewPayload();
        var message = NewInboxMessage("rejected", payload);
        var extension = receiver.AsReference<IInboxExtension>();

        var result = await extension.DeliverAsync(message, Token);

        Assert.Equal(DeliveryStatus.DeadLettered, result.Status);
        Assert.Equal(SourceSinkGrain.Rejection, result.Message);
        var (state, count, letters) = await OnGrainAsync(receiver, context => (
            Grain(context).GetState(),
            Inbox(context).Count,
            context.ActivationServices.GetRequiredService<IMessagingDiagnostics>().InboxDeadLetters));
        Assert.Empty(state.Received);
        Assert.Equal(0, count);
        var letter = Assert.Single(letters);
        Assert.Equal(payload, letter.Message.Envelope.Payload);
        Assert.Equal(1, letter.AttemptCount);
        Assert.Equal(1, Storage.CountPayloadOccurrences(Journal(receiver), payload));

        Probe.For(receiver).Mode = SinkMode.Accept;
        Assert.Equal(DeliveryStatus.Duplicate, (await extension.DeliverAsync(message, Token)).Status);
        Assert.Equal(0, Probe.For(receiver).SinkAcceptances);
        Assert.Equal(0, Probe.For(receiver).Handled);
    }

    [Fact]
    public async Task ScheduledAndDeclinedMessages_UseInboxAcceptanceAndHandlers()
    {
        var receiver = NewGrain();
        var extension = receiver.AsReference<IInboxExtension>();
        var scheduled = NewInboxMessage("scheduled", NewPayload(), DateTimeOffset.UtcNow.AddDays(1));

        Assert.Equal(DeliveryStatus.Accepted, (await extension.DeliverAsync(scheduled, Token)).Status);
        Assert.Equal(1, await OnGrainAsync(receiver, context => Inbox(context).Count));
        Assert.Equal(0, Probe.For(receiver).SinkOffers);

        Probe.For(receiver).Mode = SinkMode.Decline;
        var declinedPayload = NewPayload();
        Assert.Equal(DeliveryStatus.Accepted, (await extension.DeliverAsync(NewInboxMessage("declined", declinedPayload), Token)).Status);
        await WaitUntilAsync(receiver, _ => Probe.For(receiver).Handled == 1);
        Assert.Equal(1, Probe.For(receiver).SinkOffers);
        Assert.Equal(0, Probe.For(receiver).SinkAcceptances);
        Assert.Empty((await OnGrainAsync(receiver, context => Grain(context).GetState())).Received);
        Assert.Equal(1, Storage.CountPayloadOccurrences(Journal(receiver), declinedPayload));
    }

    private ISourceSinkGrain NewGrain() => Cluster.Client.GetGrain<ISourceSinkGrain>(Guid.NewGuid());

    private static HierarchicalKey NewMessageId(string name) => HierarchicalKey.Create("source-sink", name, Guid.NewGuid().ToString("N"));

    private static byte[] NewPayload() => RandomNumberGenerator.GetBytes(48);

    private static InboxMessage NewInboxMessage(string name, byte[] payload, DateTimeOffset? deliverAt = null) => new()
    {
        Envelope = new Envelope { MessageId = NewMessageId(name), Payload = payload, Subject = SourceSinkGrain.Subject, DeliverAt = deliverAt }
    };

    private static JournalId Journal(IAddressable grain) => JournalId.FromGrainId(grain.GetGrainId());

    private byte[][] Writes(IAddressable grain) => Storage.GetWrites(Journal(grain));

    private static SourceSinkGrain Grain(IGrainContext context) => Assert.IsType<SourceSinkGrain>(context.GrainInstance);

    private static Inbox Inbox(IGrainContext context) => context.ActivationServices.GetRequiredService<Inbox>();

    private Task<T> OnGrainAsync<T>(IAddressable grain, Func<IGrainContext, T> func)
    {
        if (!Cluster.TryGetGrainContext(grain.GetGrainId(), out var context))
        {
            throw new InvalidOperationException($"Grain '{grain.GetGrainId()}' is not active.");
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try
            {
                done.SetResult(func(context));
            }
            catch (Exception exception)
            {
                done.SetException(exception);
            }
        });
        return done.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
    }

    private async Task WaitUntilAsync(IAddressable grain, Func<IGrainContext, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!Cluster.TryGetGrainContext(grain.GetGrainId(), out _) || !await OnGrainAsync(grain, predicate))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition was not met for grain '{grain.GetGrainId()}'.");
            }

            await Task.Delay(10, Token);
        }
    }
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class JsonMessageSourceAndSinkTests(JsonMessageSourceAndSinkTests.Fixture fixture)
    : MessageSourceAndSinkTests(fixture), IClassFixture<JsonMessageSourceAndSinkTests.Fixture>
{
    public sealed class Fixture() : MessageSourceAndSinkFixture("json");
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class BinaryMessageSourceAndSinkTests(BinaryMessageSourceAndSinkTests.Fixture fixture)
    : MessageSourceAndSinkTests(fixture), IClassFixture<BinaryMessageSourceAndSinkTests.Fixture>
{
    public sealed class Fixture() : MessageSourceAndSinkFixture("orleans-binary");
}

public abstract class MessageSourceAndSinkFixture(string journalFormatKey) : IAsyncLifetime
{
    public static readonly TimeSpan DeduplicationWindow = TimeSpan.FromMinutes(10);

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    public RecordingJournalStorageProvider Storage { get; } = new();
    public SourceSinkProbe Probe { get; } = new();
    public InProcessTestCluster Cluster { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureSilo((_, silo) =>
        {
            silo.AddVolatileJournalStorage();
            silo.Services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = journalFormatKey);
            silo.Services.Configure<JsonJournalOptions>(options => options.AddTypeInfoResolver(SourceSinkJsonContext.Default));
            silo.Services.AddSingleton<TimeProvider>(Clock);
            silo.Services.UseTimeProviderForBackgroundAreas(TimeProvider.System);
            silo.UseInMemoryDurableJobs();
            silo.AddMessaging(options =>
            {
                options.DeduplicationWindow = DeduplicationWindow;
                options.MaxOutboxRetryAge = TimeSpan.FromMinutes(5);
                options.MaxDeliveryAttempts = 2;
                options.BackpressureRetryDelay = TimeSpan.FromMilliseconds(10);
            });
            silo.Services.AddSingleton(Probe);
            silo.Services.RemoveAll<IJournalStorageProvider>();
            silo.Services.RemoveAll<IJournalStorageCatalog>();
            silo.Services.AddSingleton<IJournalStorageProvider>(services =>
            {
                Storage.Configure(services.GetRequiredService<IOptions<JournaledStateManagerOptions>>());
                return Storage;
            });
            silo.Services.AddSingleton<IJournalStorageCatalog>(
                services => (IJournalStorageCatalog)services.GetRequiredService<IJournalStorageProvider>());
        });
        Cluster = builder.Build();
        await Cluster.DeployAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await Cluster.DisposeAsync();
}

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(byte[]))]
internal sealed partial class SourceSinkJsonContext : JsonSerializerContext;

public enum SinkMode
{
    Accept,
    Decline,
    Throw
}

public sealed class SourceSinkProbe
{
    private readonly ConcurrentDictionary<GrainId, Counters> _counters = new();

    public Counters For(IAddressable grain) => For(grain.GetGrainId());

    public Counters For(GrainId grainId) => _counters.GetOrAdd(grainId, static _ => new());

    public sealed class Counters
    {
        private int _materializations;
        private int _sinkOffers;
        private int _sinkAcceptances;
        private int _handled;

        public volatile SinkMode Mode;
        public int Materializations => Volatile.Read(ref _materializations);
        public int SinkOffers => Volatile.Read(ref _sinkOffers);
        public int SinkAcceptances => Volatile.Read(ref _sinkAcceptances);
        public int Handled => Volatile.Read(ref _handled);

        public void OnMaterialized() => Interlocked.Increment(ref _materializations);
        public void OnSinkOffered() => Interlocked.Increment(ref _sinkOffers);
        public void OnSinkAccepted() => Interlocked.Increment(ref _sinkAcceptances);
        public void OnHandled() => Interlocked.Increment(ref _handled);
    }
}

public interface ISourceSinkGrain : IGrainWithGuidKey
{
    Task WriteAsync();
    Task DeactivateAsync();
}

/// <summary>Stores received payloads as an inbox sink.</summary>
public sealed class SourceSinkGrain : Grain, ISourceSinkGrain, IMessagingGrain, IInboxMessageSink
{
    internal const string Subject = "source-sink/payload";
    internal const string Rejection = "The sink rejected the message.";
    private readonly IJournaledStateManager _manager;
    private readonly IDurableDictionary<string, byte[]> _outgoing;
    private readonly IDurableDictionary<string, byte[]> _received;
    private readonly IDurableDictionary<string, string> _retired;
    private readonly SourceSinkProbe.Counters _probe;

    public SourceSinkGrain(
        IGrainContext context,
        IJournaledStateManager manager,
        IInbox inbox,
        [FromKeyedServices("outgoing")] IDurableDictionary<string, byte[]> outgoing,
        [FromKeyedServices("received")] IDurableDictionary<string, byte[]> received,
        [FromKeyedServices("retired")] IDurableDictionary<string, string> retired,
        SourceSinkProbe probe)
    {
        _manager = manager;
        _outgoing = outgoing;
        _received = received;
        _retired = retired;
        _probe = probe.For(context.GrainId);
        inbox.Register(this).Register(new Handler(_probe));
    }

    internal (Dictionary<string, byte[]> Outgoing, Dictionary<string, byte[]> Received, Dictionary<string, string> Retired) GetState() =>
        (new(_outgoing), new(_received), new(_retired));

    public async Task WriteAsync() => await _manager.WriteStateAsync(CancellationToken.None);

    public Task DeactivateAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    bool IInboxMessageSink.TryAccept(InboxMessage message)
    {
        if (message.Envelope.Subject != Subject) return false;
        _probe.OnSinkOffered();
        switch (_probe.Mode)
        {
            case SinkMode.Decline:
                return false;
            case SinkMode.Throw:
                throw new InvalidOperationException(Rejection);
        }

        _received[message.Envelope.MessageId.ToString()] = message.Envelope.Payload;
        _probe.OnSinkAccepted();
        return true;
    }

    private sealed class Handler(SourceSinkProbe.Counters probe) : IInboxHandler
    {
        public bool CanHandle(IInboxHandlerContext context) => context.Message.Envelope.Subject == Subject;

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            probe.OnHandled();
            context.Complete();
            return ValueTask.CompletedTask;
        }
    }
}

public interface INoHandlerGrain : IGrainWithGuidKey
{
    Task PingAsync();
}

/// <summary>A messaging grain without handlers or sinks, whose inbox reports every delivery as handler-not-found.</summary>
public sealed class NoHandlerGrain : Grain, INoHandlerGrain, IMessagingGrain
{
    public Task PingAsync() => Task.CompletedTask;
}
