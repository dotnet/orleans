using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;

namespace Orleans.DurableMessaging.Tests.Support;

// Captures handler output in the same journal as inbox effects. Dispatch belongs to the outbox layer.
internal sealed class JournaledTestOutbox(IJournaledStateManager manager)
    : ObservedJournalDictionary<Guid, DurableEnvelope>(manager, "test-handler-output", deferred: true), IDurableOutbox
{
    private static readonly Func<DurableEnvelope, DurableEnvelope, bool> AreEquivalent = ReceiverTestServices
        .GetImplementationType("DurableEnvelopeEquivalence")
        .GetMethod("AreEquivalent")!
        .CreateDelegate<Func<DurableEnvelope, DurableEnvelope, bool>>();

    private readonly HashSet<Guid> _pending = [];
    private Guid[] _admitted = [];
    private bool _preparing;
    private ExceptionDispatchInfo? _failure;
    private PreparationBarrier? _nextPreparation;
    public Exception? Failure => _failure?.SourceException;
    public int SendCalls { get; private set; }
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

    public IEnumerable<DurableEnvelope> Messages => Values;
    public void Send(DurableEnvelope envelope)
    {
        SendCalls++;
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

        Add(envelope.MessageId, envelope);
        _pending.Add(envelope.MessageId);
    }

    public bool TryGetMessage(Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope) =>
        TryGetValue(messageId, out envelope);

    public override bool IsWritePrepared
    {
        get
        {
            _failure?.Throw();
            return _nextPreparation is null && !_preparing;
        }
    }

    public override async ValueTask PrepareWriteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PreparationScheduler = TaskScheduler.Current;
        PreparationContext = ReceiverTestServices.CurrentGrainContext;
        _preparing = true;
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
        _preparing = false;
    }

    public override void AppendEntries(JournalStreamWriter writer)
    {
        base.AppendEntries(writer);
        Capture();
    }

    public override void AppendSnapshot(JournalStreamWriter writer)
    {
        base.AppendSnapshot(writer);
        Capture();
    }

    private void Capture()
    {
        _admitted = _pending.ToArray();
        _pending.Clear();
        LastCapturedIds = _admitted;
    }

    public override void OnWriteCompleted()
    {
        base.OnWriteCompleted();
        _admitted = [];
        AfterWriteCompleted?.Invoke();
    }

    public override void OnFaulted(Exception exception)
    {
        _failure ??= ExceptionDispatchInfo.Capture(exception);
        base.OnFaulted(exception);
    }

    public override void Reset(JournalStreamWriter writer)
    {
        base.Reset(writer);
        _pending.Clear();
        _admitted = [];
        _preparing = false;
    }
}
