using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Orleans.Journaling;

namespace Orleans.DurableMessaging.Tests.Support;

// Captures handler output in the same journal as inbox effects. Dispatch belongs to the outbox layer.
internal sealed class JournaledTestOutbox(IJournaledStateManager manager)
    : ObservedJournalDictionary<Guid, DurableEnvelope>(manager, deferred: true), IDurableOutbox
{
    private static readonly Func<DurableEnvelope, DurableEnvelope, bool> AreEquivalent = ReceiverTestServices
        .GetImplementationType("DurableEnvelopeEquivalence")
        .GetMethod("AreEquivalent")!
        .CreateDelegate<Func<DurableEnvelope, DurableEnvelope, bool>>();

    private readonly HashSet<Guid> _pending = [];
    private Guid[] _admitted = [];
    private ExceptionDispatchInfo? _failure;
    private PreparationBarrier? _nextPreparation;
    private readonly List<PreparationOperation> _preparations = [];
    private readonly List<BatchObservation> _batches = [];
    public Exception? Failure => _failure?.SourceException;
    public int SendCalls { get; private set; }
    public Exception? NextSendFailure { get; set; }
    public int PreparationsStarted => _preparations.Count;
    public int PreparationsCompleted { get; private set; }
    public IReadOnlyList<PreparationOperation> Preparations => _preparations;
    public IReadOnlyList<BatchObservation> PreparedBatches => _batches;
    public int JournalPreparationCalls { get; private set; }
    public IReadOnlyList<Guid> LastCapturedIds { get; private set; } = [];
    public Action? AfterWriteCompleted { get; set; }

    // This gates explicit local acquisition only, never journal execution or capture.
    public PreparationBarrier BlockNextPreparation(bool ignoreCancellation = false)
    {
        if (_nextPreparation is not null)
        {
            throw new InvalidOperationException("An outbox preparation barrier is already armed.");
        }
        return _nextPreparation = new PreparationBarrier(ignoreCancellation);
    }

    public sealed class PreparationBarrier(bool ignoreCancellation) : IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IgnoreCancellation { get; } = ignoreCancellation;
        public PreparationOperation? Operation { get; internal set; }
        public Task WaitAsync() => Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        public void Release() => Continue.TrySetResult();
        public void Fail(Exception exception) => Continue.TrySetException(exception);
        public void CompleteSuccess() => Release();
        public void CompleteFailure(Exception exception) => Fail(exception);
        public void Dispose() => Release();
    }

    public sealed class PreparationOperation(int id)
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id { get; } = id;
        public TaskScheduler StartedScheduler { get; } = TaskScheduler.Current;
        public IGrainContext? StartedContext { get; } = ReceiverTestServices.CurrentGrainContext;
        public TaskScheduler? CompletedScheduler { get; private set; }
        public IGrainContext? CompletedContext { get; private set; }
        public Task Completed => _completed.Task;
        public Exception? Failure { get; internal set; }
        public BatchObservation? Batch { get; internal set; }

        internal void Complete()
        {
            CompletedScheduler = TaskScheduler.Current;
            CompletedContext = ReceiverTestServices.CurrentGrainContext;
            _completed.TrySetResult();
        }
    }

    // Probe data is separate from the opaque handle returned to consumers.
    public sealed class BatchObservation(int id, int preparationId, Guid[] messageIds)
    {
        private int _disposeCalls;
        public int Id { get; } = id;
        public int PreparationId { get; } = preparationId;
        public IReadOnlyList<Guid> MessageIds { get; } = messageIds;
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public bool IsDisposed => DisposeCalls != 0;
        public bool IsStaged { get; internal set; }
        internal void RecordDisposal() => Interlocked.Increment(ref _disposeCalls);
    }

    private sealed class PreparedBatch(
        JournaledTestOutbox owner,
        DurableEnvelope[] messages,
        BatchObservation observation) : IPreparedOutboxBatch
    {
        public JournaledTestOutbox Owner { get; } = owner;
        public DurableEnvelope[] Messages { get; } = messages;
        public BatchObservation Observation { get; } = observation;
        public void Dispose() => Observation.RecordDisposal();
    }

    public IEnumerable<DurableEnvelope> Messages => Values;

    public async ValueTask<IPreparedOutboxBatch> PrepareSendAsync(
        IReadOnlyList<DurableEnvelope> messages,
        CancellationToken cancellationToken = default)
    {
        var operation = new PreparationOperation(_preparations.Count + 1);
        _preparations.Add(operation);
        try
        {
            _failure?.Throw();
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(messages);

            // Snapshot the collection before the first wait. Envelope data remains the supplied
            // serialized data; this collaborator does not introduce a second serialization policy.
            var snapshot = messages.ToArray();
            ValidateMessages(snapshot);
            var barrier = _nextPreparation;
            _nextPreparation = null;
            if (barrier is not null)
            {
                barrier.Operation = operation;
                barrier.Entered.TrySetResult();
                if (barrier.IgnoreCancellation)
                {
                    // Deliberate provider behavior for retirement/late-result tests.
                    await barrier.Continue.Task;
                }
                else
                {
                    await barrier.Continue.Task.WaitAsync(cancellationToken);
                }
            }

            // Even if the attempt closed during the wait, publish the controlled local result.
            // The receiver must observe/dispose it; Send still checks the owner's retained fault.
            var observation = new BatchObservation(
                _batches.Count + 1, operation.Id, snapshot.Select(static message => message.MessageId).ToArray());
            var batch = new PreparedBatch(this, snapshot, observation);
            _batches.Add(observation);
            operation.Batch = observation;
            return batch;
        }
        catch (Exception exception)
        {
            operation.Failure = exception;
            throw;
        }
        finally
        {
            PreparationsCompleted++;
            operation.Complete();
        }
    }

    public void Send(IPreparedOutboxBatch batch)
    {
        SendCalls++;
        _failure?.Throw();
        ArgumentNullException.ThrowIfNull(batch);
        if (batch is not PreparedBatch prepared || !ReferenceEquals(prepared.Owner, this))
        {
            throw new InvalidOperationException("The prepared outbox batch belongs to another owner.");
        }
        if (prepared.Observation.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(IPreparedOutboxBatch));
        }
        if (prepared.Observation.IsStaged)
        {
            return;
        }
        if (NextSendFailure is { } failure)
        {
            NextSendFailure = null;
            throw failure;
        }

        // Another batch can have staged since acquisition. Revalidate every item before adding
        // any prefix, preserving the original retained envelope for equivalent duplicate IDs.
        ValidateMessages(prepared.Messages);
        foreach (var envelope in prepared.Messages)
        {
            if (!ContainsKey(envelope.MessageId))
            {
                Add(envelope.MessageId, envelope);
                _pending.Add(envelope.MessageId);
            }
        }
        prepared.Observation.IsStaged = true;
    }

    private void ValidateMessages(IReadOnlyList<DurableEnvelope> messages)
    {
        var batch = new Dictionary<Guid, DurableEnvelope>();
        foreach (var envelope in messages)
        {
            if (envelope is not { MessageId: var messageId, RouteKey: var route, Data: not null }
                || messageId == Guid.Empty || string.IsNullOrWhiteSpace(route))
            {
                throw new ArgumentException("An outgoing envelope must have a message ID, route and serialized data.", nameof(messages));
            }
            if ((TryGetMessage(envelope.MessageId, out var existing) && !AreEquivalent(existing, envelope))
                || (batch.TryGetValue(envelope.MessageId, out var duplicate) && !AreEquivalent(duplicate, envelope)))
            {
                throw new InvalidOperationException(
                    $"The durable outbox already contains a different envelope with message ID '{envelope.MessageId}'.");
            }
            batch.TryAdd(envelope.MessageId, envelope);
        }
    }

    public bool TryGetMessage(Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope) =>
        TryGetValue(messageId, out envelope);

    public override bool IsWritePrepared
    {
        get
        {
            _failure?.Throw();
            return true;
        }
    }

    public override ValueTask PrepareWriteAsync(CancellationToken cancellationToken)
    {
        // Temporary foundation bridge: no outgoing acquisition belongs to journal execution.
        JournalPreparationCalls++;
        _failure?.Throw();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public override void WritePendingEntries(JournalStreamWriter writer)
    {
        base.WritePendingEntries(writer);
        Capture();
    }

    public override void WriteSnapshot(JournalStreamWriter writer)
    {
        base.WriteSnapshot(writer);
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
    }
}
