using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;

namespace Orleans.DurableMessaging.Tests.Support;

// Captures handler output in the same journal as inbox effects. Dispatch belongs to the outbox layer.
internal sealed class JournaledTestOutbox(
    [FromKeyedServices("test-handler-output")] IDurableDictionary<Guid, DurableEnvelope> messages) : IDurableOutbox, IJournaledStateObserver
{
    private static readonly Func<DurableEnvelope, DurableEnvelope, bool> AreEquivalent = ReceiverTestServices
        .GetImplementationType("DurableEnvelopeEquivalence")
        .GetMethod("AreEquivalent")!
        .CreateDelegate<Func<DurableEnvelope, DurableEnvelope, bool>>();

    private readonly Dictionary<Guid, DurableEnvelope> _pending = [];
    private KeyValuePair<Guid, DurableEnvelope>[] _admitted = [];
    private ExceptionDispatchInfo? _failure;
    private PreparationBarrier? _nextPreparation;
    public Exception? Failure => _failure?.SourceException;
    public IReadOnlyList<Guid> LastCapturedIds { get; private set; } = [];
    public Action? BeforeFinalization { get; set; }
    public Action? AfterWriteCompleted { get; set; }
    public TaskScheduler? PreparationScheduler { get; private set; }
    public TaskScheduler? ContinuationScheduler { get; private set; }
    public IGrainContext? PreparationContext { get; private set; }
    public IGrainContext? ContinuationContext { get; private set; }

    public PreparationBarrier BlockNextPreparation()
    {
        if (_nextPreparation is not null)
        {
            throw new InvalidOperationException("An outbox preparation barrier is already armed.");
        }
        return _nextPreparation = new PreparationBarrier();
    }

    public sealed class PreparationBarrier : IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitAsync() => Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        public void Release() => Continue.TrySetResult();
        public void Fail(Exception exception) => Continue.TrySetException(exception);
        public void Dispose() => Release();
    }

    public int Count => messages.Count + _pending.Keys.Count(key => !messages.ContainsKey(key));
    public IEnumerable<DurableEnvelope> Messages => messages.Values.Concat(_pending.Where(pair => !messages.ContainsKey(pair.Key)).Select(static pair => pair.Value));
    public void Send(DurableEnvelope envelope)
    {
        _failure?.Throw();
        if (TryGetMessage(envelope.MessageId, out var existing))
        {
            if (!AreEquivalent(existing, envelope))
            {
                throw new InvalidOperationException(
                    $"The durable outbox already contains a different envelope with message ID '{envelope.MessageId}'.");
            }

            return;
        }

        _pending.Add(envelope.MessageId, envelope);
    }

    public bool TryGetMessage(Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope) =>
        _pending.TryGetValue(messageId, out envelope) || messages.TryGetValue(messageId, out envelope);

    public async ValueTask OnWritePreparingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PreparationScheduler = TaskScheduler.Current;
        PreparationContext = ReceiverTestServices.CurrentGrainContext;
        _admitted = _pending.ToArray();
        var barrier = _nextPreparation;
        _nextPreparation = null;
        if (barrier is not null)
        {
            barrier.Entered.TrySetResult();
            await barrier.Continue.Task.WaitAsync(cancellationToken);
        }
        ContinuationScheduler = TaskScheduler.Current;
        ContinuationContext = ReceiverTestServices.CurrentGrainContext;
        BeforeFinalization?.Invoke();
    }

    public void FinalizeWrite(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var entry in _admitted)
        {
            messages.Add(entry.Key, entry.Value);
        }
    }

    public void OnWriteStarted() => LastCapturedIds = _admitted.Select(static pair => pair.Key).ToArray();
    public void OnWriteCompleted()
    {
        foreach (var entry in _admitted)
        {
            _pending.Remove(entry.Key);
        }
        _admitted = [];
        AfterWriteCompleted?.Invoke();
    }
    public void OnRecoveryCompleted() { }
    public void OnFaulted(Exception exception) => _failure = ExceptionDispatchInfo.Capture(exception);
    public void OnDeleteCompleted()
    {
        _pending.Clear();
        _admitted = [];
    }
}
