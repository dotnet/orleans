using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.Messaging.Tests.Support;

public sealed class BootstrapDeliveryProbe : IOutgoingGrainCallFilter, ILoggerProvider
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<GrainId, TaskCompletionSource<(HierarchicalKey MessageId, int Value)>> _outputs = new();

    private readonly ConcurrentDictionary<GrainId, TaskCompletionSource> _drains = new();

    public Task WaitForDrainAsync(GrainId sender) =>
        _drains.GetOrAdd(sender, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(30));

    public ILogger CreateLogger(string categoryName) => new CompletionLogger(this, categoryName);
    public void Dispose() { }

    private sealed class CompletionLogger(BootstrapDeliveryProbe owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => category == "Orleans.Messaging.Outbox";
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel) && eventId.Name == "LogDeliveryComplete"
                && state is IEnumerable<KeyValuePair<string, object?>> fields
                && fields.Any(field => field.Key == "RemainingCount" && field.Value is 0)
                && ReceiverTestServices.CurrentGrainContext is { } context)
            {
                owner._drains.GetOrAdd(context.GrainId, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            }
        }
    }

    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        if (context.TargetId == BootstrapState.OutputTarget && context.MethodName == nameof(IInboxExtension.DeliverAsync))
        {
            await _release.Task.WaitAsync(context.Request.GetCancellationToken());
        }
        await context.Invoke();
    }

    public void Release() => _release.TrySetResult();
    public Task<(HierarchicalKey MessageId, int Value)> WaitForOutputAsync(GrainId sender) =>
        GetOutput(sender).Task.WaitAsync(TimeSpan.FromSeconds(30));
    public void OnOutput(GrainId sender, HierarchicalKey messageId, int value) => GetOutput(sender).TrySetResult((messageId, value));
    private TaskCompletionSource<(HierarchicalKey MessageId, int Value)> GetOutput(GrainId sender) =>
        _outputs.GetOrAdd(sender, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
}

public interface IBootstrapOutputGrain : IGrainWithStringKey
{
    Task<int> GetMessageCountAsync();
}

[GrainType("bootstrap-output")]
public sealed class BootstrapOutputGrain : Grain, IBootstrapOutputGrain, IMessagingGrain, IInboxHandler,
    IStateMachine, IDurableDictionaryCommandHandler<HierarchicalKey, int>
{
    private readonly Dictionary<HierarchicalKey, int> _values = [];
    private readonly List<(GrainId Sender, HierarchicalKey MessageId, int Value)> _pending = [];
    private (GrainId Sender, HierarchicalKey MessageId, int Value)[] _captured = [];
    private readonly BootstrapDeliveryProbe _probe;
    private readonly Orleans.Serialization.Session.SerializerSessionPool _sessions;
    private readonly Orleans.Serialization.Serializer<GrainId> _senderSerializer;
    private readonly IDurableDictionaryCommandCodec<HierarchicalKey, int> _codec;

    public BootstrapOutputGrain(IJournaledStateManager manager, IInbox inbox, BootstrapDeliveryProbe probe,
        IDurableDictionaryCommandCodec<HierarchicalKey, int> codec, Orleans.Serialization.Session.SerializerSessionPool sessions, Orleans.Serialization.Serializer<GrainId> senderSerializer)
    {
        _sessions = sessions;
        _senderSerializer = senderSerializer;
        _probe = probe;
        _codec = codec;
        manager.RegisterStateMachine("bootstrap-output-values", this);
        inbox.Register(this);
    }

    public bool CanHandle(IInboxHandlerContext context) => true;


    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = (int)(TestApplicationProtocol.Read(_sessions, context.Message).Body
            ?? throw new InvalidOperationException("The bootstrap output must contain an integer."));
        if (context.Message.Envelope.Headers is not { } headers || !headers.TryGetValue("sender", out var senderBytes))
        {
            throw new InvalidOperationException("The bootstrap output must supply its sender.");
        }
        var sender = _senderSerializer.Deserialize(senderBytes);
        _values.Add(context.Message.Envelope.MessageId, value);
        _pending.Add((sender, context.Message.Envelope.MessageId, value));
        context.Complete();
        return ValueTask.CompletedTask;
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

    public void ApplySet(HierarchicalKey key, int value) => _values[key] = value;
    public void ApplyRemove(HierarchicalKey key) => _values.Remove(key);
    public void ApplyClear() => _values.Clear();
    public void Reset(int capacityHint) => _values.Clear();
}
