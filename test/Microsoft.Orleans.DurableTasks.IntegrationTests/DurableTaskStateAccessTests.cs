using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Orleans.Messaging;
using Orleans.Serialization;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>Runs journaled state access coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskStateAccessTests() : DurableTaskStateAccessTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs journaled state access coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskStateAccessTests() : DurableTaskStateAccessTests(DurableTaskRecoveryFixture.OrleansBinary);

/// <summary>
/// Verifies that a durable task body which reads and changes journaled state and sends through the outbox, without
/// <c>DurableTask.RunInline</c>, replays after a deactivation without applying its changes or sending again.
/// </summary>
public abstract class DurableTaskStateAccessTests : IAsyncLifetime
{
    // Every silo shares the probe, so the test observes the grain wherever it activates.
    private readonly DurableTaskStateAccessProbe _probe = new();
    private readonly DurableTaskRecoveryFixture _fixture;

    protected DurableTaskStateAccessTests(string journalFormatKey)
    {
        var probe = _probe;
        _fixture = new(journalFormatKey, siloBuilder =>
        {
            siloBuilder.Services.AddSingleton(probe);
            siloBuilder.Services.Configure<JsonJournalOptions>(static options => options.AddTypeInfoResolver(DurableTaskStateAccessJsonContext.Default));
        });
    }

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task ReplayAfterDeactivationDecidesFromRecordedStateAndDoesNotSendAgain()
    {
        const string rootId = "reserve-and-notify";
        var cancellationToken = TestContext.Current.CancellationToken;
        var probe = _probe;
        var grain = _fixture.Client.GetGrain<IDurableTaskStateAccessTestGrain>(Guid.NewGuid());
        var grainId = grain.GetGrainId();
        await grain.SetStockAsync(1);

        _ = await grain.ReserveAndNotifyAsync().WithId(rootId).ScheduleAsync(cancellationToken);
        await WaitAsync(() => probe.GetNotifications(grainId).Count == 1, null);
        Assert.Equal(0, await grain.GetStockAsync());

        // Ordinary grain work restocks the item while the body waits on a long delay. The call doesn't run in the body's
        // context, so the body's access log doesn't record its write. The next activation replays the body.
        Assert.False(await grain.IsWithinDurableTaskAsync());
        await grain.SetStockAsync(5);
        var attempts = probe.GetAttempts(grainId);
        await grain.RequestDeactivationAsync();
        await WaitAsync(() => probe.GetAttempts(grainId) > attempts, () => grain.GetStockAsync());

        // The replay observed the level which the first run read, so it neither decremented the restocked level nor sent
        // another notification: the send's record holds the recorded message identifier, which the replay's effect returns.
        Assert.Equal(5, await grain.GetStockAsync());
        Assert.Equal([1], probe.GetNotifications(grainId));
        Assert.Equal([1, 1], probe.GetObservedLevels(grainId));
        await grain.GetDurableTask<int>(rootId).CancelAsync(cancellationToken);

        async Task WaitAsync(Func<bool> condition, Func<Task>? poke)
        {
            try
            {
                await probe.WaitAsync(condition, cancellationToken, poke);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var task = grain.GetDurableTask<int>(rootId);
                var completed = await task.IsCompletedAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, cancellationToken);
                var response = completed ? await task.GetResponseAsync(cancellationToken) : null;
                Assert.Fail(
                    $"Timed out. Task completed: {completed}, status: {response?.Status}, exception: {response?.Exception}. Attempts: [{string.Join(", ", probe.GetObservedLevels(grainId))}], "
                    + $"notifications: [{string.Join(", ", probe.GetNotifications(grainId))}], stock: {await grain.GetStockAsync()}.");
            }
        }
    }
}

public interface IDurableTaskStateAccessTestGrain : IGrainWithGuidKey
{
    DurableTask<int> ReserveAndNotifyAsync();
    Task SetStockAsync(int level);
    Task<int> GetStockAsync();
    Task<bool> IsWithinDurableTaskAsync();
    Task RequestDeactivationAsync();
}

public sealed class DurableTaskStateAccessProbe
{
    private readonly ConcurrentDictionary<GrainId, List<int>> _notifications = [];
    private readonly ConcurrentDictionary<GrainId, List<int>> _observedLevels = [];
    private int _nextMessageId;

    /// <summary>Issues a new message identifier on every call, which a body records with <c>DurableTask.RunInline</c>.</summary>
    public MessageId NextMessageId() => MessageId.Create("reserved", Interlocked.Increment(ref _nextMessageId).ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void RecordAttempt(GrainId grainId, int observedLevel) => Add(_observedLevels, grainId, observedLevel);

    public void RecordNotification(GrainId grainId, int level) => Add(_notifications, grainId, level);

    public int GetAttempts(GrainId grainId) => Get(_observedLevels, grainId).Count;

    public IReadOnlyList<int> GetObservedLevels(GrainId grainId) => Get(_observedLevels, grainId);

    public IReadOnlyList<int> GetNotifications(GrainId grainId) => Get(_notifications, grainId);

    public async Task WaitAsync(Func<bool> condition, CancellationToken cancellationToken, Func<Task>? poke = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            if (poke is not null)
            {
                await poke();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    private static void Add(ConcurrentDictionary<GrainId, List<int>> values, GrainId grainId, int value)
    {
        var list = values.GetOrAdd(grainId, static _ => []);
        lock (list)
        {
            list.Add(value);
        }
    }

    private static IReadOnlyList<int> Get(ConcurrentDictionary<GrainId, List<int>> values, GrainId grainId)
    {
        if (!values.TryGetValue(grainId, out var list))
        {
            return [];
        }

        lock (list)
        {
            return [.. list];
        }
    }
}

[GrainType("durable-task-state-access-test")]
public sealed class DurableTaskStateAccessTestGrain : DurableGrain, IDurableTaskStateAccessTestGrain, IInboxHandler
{
    private const string Subject = "durable-task-state-access.reserved";
    private readonly DurableTaskStateAccessProbe _probe;
    private readonly IDurableValue<int> _stock;
    private readonly IOutbox _outbox;
    private readonly MessageType<int> _reserved;

    public DurableTaskStateAccessTestGrain(
        DurableTaskStateAccessProbe probe,
        [FromKeyedServices("stock")] IDurableValue<int> stock,
        IOutbox outbox,
        IInbox inbox,
        Serializer serializer)
    {
        _probe = probe;
        _stock = stock;
        _outbox = outbox;
        _reserved = new(Subject, serializer);
        inbox.Register(this);
    }

    public async DurableTask<int> ReserveAndNotifyAsync()
    {
        var level = _stock.Value;
        if (level > 0)
        {
            _stock.Value = level - 1;
            // A replay sends with the same arguments as the recording run, so the identifier comes from a recorded effect.
            var messageId = DurableTask.RunInline("message-id", _probe.NextMessageId);
            _outbox.Send(_reserved, messageId, this.GetGrainId(), level);
        }

        _probe.RecordAttempt(this.GetGrainId(), level);
        await DurableTask.Delay(TimeSpan.FromHours(1)).WithId("hold");
        return level;
    }

    public async Task SetStockAsync(int level)
    {
        _stock.Value = level;
        await WriteStateAsync();
    }

    public Task<int> GetStockAsync() => Task.FromResult(_stock.Value);

    public Task<bool> IsWithinDurableTaskAsync() => Task.FromResult(DurableTaskContext.TryGetCurrent(out _));

    public Task RequestDeactivationAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    bool IInboxHandler.CanHandle(InboxMessage message) => message.Envelope.Subject == Subject;

    ValueTask IInboxHandler.HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        _probe.RecordNotification(this.GetGrainId(), _reserved.Decode(context.Message.Envelope));
        context.Complete();
        return default;
    }
}

[JsonSerializable(typeof(int))]
internal sealed partial class DurableTaskStateAccessJsonContext : JsonSerializerContext;
