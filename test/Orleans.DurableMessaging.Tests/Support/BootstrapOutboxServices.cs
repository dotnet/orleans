using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableMessaging.Tests.Support;

internal static class BootstrapOutboxServices
{
    public const string JobName = "orleans.messaging.outbox-flush";
    public const string StateName = "__orleans.durable-messaging.outbox";
    public static readonly string[] StateNames =
    [
        StateName,
        "__orleans.durable-messaging.outbox-message-state",
        "__orleans.durable-messaging.outbox-dead-letters",
        "__orleans.durable-messaging.outbox-job-id",
        "__orleans.durable-messaging.outbox-job-handle",
        "__orleans.durable-messaging.outbox-completed-job-id",
        "__orleans.durable-messaging.outbox-job-sequence"
    ];

    public static void Add(IServiceCollection services)
    {
        var outboxType = ReceiverTestServices.GetImplementationType("DurableOutbox");
        services.RemoveAll<IDurableOutbox>();
        services.RemoveAllKeyed<IDurableOutbox>(KeyedService.AnyKey);
        services.RemoveAllKeyed<IDurableDictionary<Guid, DurableEnvelope>>("test-handler-output");
        services.TryAddScoped(outboxType);
        services.AddScoped<IDurableOutbox>(provider => (IDurableOutbox)provider.GetRequiredService(outboxType));
        AddAlias(typeof(IDurableDictionary<Guid, DurableEnvelope>), StateNames[0], "MessageState");
        AddAlias(typeof(IDurableDictionary<,>).MakeGenericType(typeof(Guid), ReceiverTestServices.GetImplementationType("OutboxMessageState")), StateNames[1], "AttemptState");
        AddAlias(typeof(IDurableDictionary<,>).MakeGenericType(typeof(Guid), ReceiverTestServices.GetImplementationType("OutboxDeadLetter")), StateNames[2], "DeadLetterState");
        AddAlias(typeof(IDurableValue<string>), StateNames[3], "JobIdState");
        AddAlias(typeof(IDurableValue<Orleans.DurableJobs.DurableJob>), StateNames[4], "JobState");
        AddAlias(typeof(IDurableValue<string>), StateNames[5], "CompletedJobIdState");
        AddAlias(typeof(IDurableValue<long>), StateNames[6], "JobSequenceState");

        void AddAlias(Type serviceType, string key, string property)
        {
            var getter = outboxType.GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!;
            services.RemoveAllKeyed(serviceType, key);
            services.AddKeyedScoped(serviceType, key, (provider, _) => getter.GetValue(provider.GetRequiredService(outboxType))!);
        }
    }


}

public sealed class BootstrapDeliveryProbe : IOutgoingGrainCallFilter
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<GrainId, TaskCompletionSource<(Guid MessageId, int Value)>> _outputs = new();

    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        if (context.TargetId == BootstrapState.OutputTarget && context.MethodName == nameof(IDurableInboxExtension.DeliverAsync))
        {
            await _release.Task.WaitAsync(context.Request.GetCancellationToken());
        }
        await context.Invoke();
    }

    public void Release() => _release.TrySetResult();
    public Task<(Guid MessageId, int Value)> WaitForOutputAsync(GrainId sender) =>
        GetOutput(sender).Task.WaitAsync(TimeSpan.FromSeconds(30));
    public void OnOutput(GrainId sender, Guid messageId, int value) => GetOutput(sender).TrySetResult((messageId, value));
    private TaskCompletionSource<(Guid MessageId, int Value)> GetOutput(GrainId sender) =>
        _outputs.GetOrAdd(sender, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
}

public interface IBootstrapOutputGrain : IGrainWithStringKey
{
    Task<int> GetMessageCountAsync();
}

[GrainType("bootstrap-output")]
public sealed class BootstrapOutputGrain : Grain, IBootstrapOutputGrain, IDurableMessagingGrain, IInboxHandler,
    IStateMachine, IDurableDictionaryCommandHandler<Guid, int>
{
    private readonly Dictionary<Guid, int> _values = [];
    private readonly List<(GrainId Sender, Guid MessageId, int Value)> _pending = [];
    private (GrainId Sender, Guid MessageId, int Value)[] _captured = [];
    private readonly BootstrapDeliveryProbe _probe;
    private readonly IDurableDictionaryCommandCodec<Guid, int> _codec;

    public BootstrapOutputGrain(IJournaledStateManager manager, IDurableInbox inbox, BootstrapDeliveryProbe probe)
    {
        _probe = probe;
        _codec = manager.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<Guid, int>>();
        manager.RegisterStateMachine("bootstrap-output-values", this);
        inbox.RegisterHandler("output", this);
    }

    public bool CanHandle(IInboxHandlerContext context) => context.Envelope.RouteKey == "output";
    public ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!context.Envelope.Data.TryGetBody<int>(out var value))
        {
            throw new InvalidOperationException("The bootstrap output must contain an integer.");
        }
        return ValueTask.FromResult<Action>(() =>
        {
            _values.Add(context.Envelope.MessageId, value);
            _pending.Add((context.Envelope.SenderId, context.Envelope.MessageId, value));
        });
    }

    public Task<int> GetMessageCountAsync() => Task.FromResult(_values.Count);
    public void WritePendingEntries(JournalStreamWriter writer)
    {
        _captured = _pending.ToArray();
        _pending.Clear();
        foreach (var item in _captured) _codec.WriteSet(item.MessageId, item.Value, writer);
    }
    public void WriteSnapshot(JournalStreamWriter writer)
    {
        _captured = _pending.ToArray();
        _pending.Clear();
        _codec.WriteSnapshot(_values, writer);
    }
    public void OnWriteCompleted()
    {
        foreach (var output in _captured) _probe.OnOutput(output.Sender, output.MessageId, output.Value);
        _captured = [];
    }
    public void Reset(JournalStreamWriter writer)
    {
        _values.Clear();
        _pending.Clear();
        _captured = [];
    }
    public void ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    public void ApplySet(Guid key, int value) => _values[key] = value;
    public void ApplyRemove(Guid key) => _values.Remove(key);
    public void ApplyClear() => _values.Clear();
    public void Reset(int capacityHint) => _values.Clear();
}
