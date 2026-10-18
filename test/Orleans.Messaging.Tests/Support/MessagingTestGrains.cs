using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Serialization;
using Orleans.Serialization.Session;

namespace Orleans.Messaging.Tests.Support;

public interface IMessagingTestGrain : IGrainWithGuidKey
{
    Task<MessageId> SendAsync(GrainId target, string route, TestMessage message);
    Task<MessageId> SendDuplicateAsync(GrainId target, string route, TestMessage message);
    Task<MessageId> SendAndDeactivateAsync(GrainId target, string route, TestMessage message);
    Task<MessageId> StageWithoutCommitAsync(GrainId target, string route, TestMessage message);
    Task RetryWriteStateAsync();
    Task StageEffectAsync(MessageEffect effect);
    Task StageOutputAsync(OutboxMessage envelope);
    Task<DeliveryResult> AcceptAndDeactivateAsync(InboxMessage envelope);
    Task ConfigureHandlerAsync(bool enabled);
    Task<bool> RemoveInboxDeadLetterAsync(MessageId messageId);
    Task<EndpointSnapshot> GetSnapshotAsync();
    Task RequestDeactivationAsync();
    Task SetControlEnvelopeAsync(InboxMessage envelope);
    Task DeleteStateAndDeactivateAsync();
    Task HoldPumpTurnAsync(string barrierRoute, bool deactivate);
    Task HoldPumpTurnAsync(string barrierRoute, InboxMessage replacement, bool deactivate);
}

[GenerateSerializer, Immutable]
public sealed record TestMessage(
    [property: Id(0)] MessageId LogicalId,
    [property: Id(1)] int Sequence,
    [property: Id(2)] string Value,
    [property: Id(3)] GrainId? ForwardTo = null,
    [property: Id(8)] bool ThrowDuringPreparation = false,
    [property: Id(5)] bool CommitDuringHandling = false,
    [property: Id(6)] bool DeleteDuringHandling = false,
    [property: Id(9)] bool ThrowOnceDuringPreparation = false,
    [property: Id(10)] int ThrowForAttempts = 0);

[GenerateSerializer, Immutable]
public sealed record MessageEffect(
    [property: Id(0)] MessageId LogicalId,
    [property: Id(1)] int Count,
    [property: Id(2)] int Sequence,
    [property: Id(3)] string Value);

[GenerateSerializer, Immutable]
public sealed record EndpointSnapshot(
    [property: Id(0)] Guid ActivationId,
    [property: Id(1)] string SiloAddress,
    [property: Id(2)] int InboxCount,
    [property: Id(3)] int OutboxCount,
    [property: Id(4)] int MaxConcurrentHandlers,
    [property: Id(5)] IReadOnlyList<MessageEffect> Effects,
    [property: Id(6)] IReadOnlyList<DeadLetterSnapshot> InboxDeadLetters,
    [property: Id(7)] IReadOnlyList<DeadLetterSnapshot> OutboxDeadLetters,
    [property: Id(9)] int ProcessedMessageCount);

[GenerateSerializer, Immutable]
public sealed record DeadLetterSnapshot(
    [property: Id(0)] MessageId MessageId,
    [property: Id(1)] string Route,
    [property: Id(2)] string Reason,
    [property: Id(3)] int AttemptCount,
    [property: Id(4)] DateTimeOffset DeadLetteredAt);

[GrainType("messaging-inbox-test")]
public sealed class MessagingTestGrain : DurableGrain, IMessagingTestGrain, IDurableJobHandler,
    IObserver<GrainLifecycleEvents.LifecycleEvent>, IDisposable
{
    private readonly IGrainContext _grainContext;
    private readonly IDisposable _lifecycleSubscription;
    private readonly IJournaledStateManager _journalOwner;
    private readonly IOutbox _outbox;
    private readonly IMessagingDiagnostics _diagnostics;
    private readonly IDurableDictionary<MessageId, MessageEffect> _effects;
    private readonly Inbox _inboxState;
    private readonly IInbox _inbox;
    private readonly SerializerSessionPool _sessions;
    private readonly TestHandlerConfiguration _handlerConfiguration;
    internal IInboxHandler? HandlerOverride { get; set; }
    private readonly JournalProbeState _journalProbe;
    internal Exception? JournalPreparationFailure
    {
        get => _journalProbe.PreparationFailure;
        set => _journalProbe.PreparationFailure = value;
    }
    private readonly ILocalSiloDetails _siloDetails;
    private readonly HandlerProbe _handlerProbe;
    private readonly SnapshotProbe _snapshotProbe;
    private readonly Guid _activationId = Guid.NewGuid();
    private int _activeHandlers;
    private int _maxConcurrentHandlers;
    private bool _handlerRegistered;
    // Spans activations so a replayed message does not fail again after recovery.
    private static readonly ConcurrentDictionary<MessageId, byte> FailedOnce = new();
    private static readonly ConcurrentDictionary<MessageId, int> FailedAttempts = new();

    public MessagingTestGrain(
        IGrainContext grainContext,
        IJournaledStateManager journalOwner,
        IInbox inbox,
        IOutbox outbox,
        IMessagingDiagnostics diagnostics,
        [FromKeyedServices("test-effects")] IDurableDictionary<MessageId, MessageEffect> effects,
        [FromKeyedServices("inbox")] IDurableValue<string> applicationInboxState,


        SerializerSessionPool sessions,
        TestHandlerConfiguration handlerConfiguration,
        ILocalSiloDetails siloDetails,
        HandlerProbe handlerProbe,
        SnapshotProbe snapshotProbe)
    {
        _grainContext = grainContext;
        _journalOwner = journalOwner;
        _inbox = inbox;
        _outbox = outbox;
        _diagnostics = diagnostics;
        _effects = effects;
        ArgumentNullException.ThrowIfNull(applicationInboxState);

        _inboxState = grainContext.ActivationServices.GetRequiredService<Inbox>();

        _sessions = sessions;
        _handlerConfiguration = handlerConfiguration;
        _siloDetails = siloDetails;
        _handlerProbe = handlerProbe;
        _snapshotProbe = snapshotProbe;
        _lifecycleSubscription = GrainLifecycleEvents.AllEvents.Subscribe(this);
        _journalProbe = new JournalProbeState(journalOwner);
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (_handlerConfiguration.IsEnabled(this.GetGrainId()))
        {
            RegisterHandler();
        }
        await base.OnActivateAsync(cancellationToken);
        _snapshotProbe.Publish(this.GetGrainId(), CreateSnapshot());
    }

    public async Task<MessageId> SendAsync(GrainId target, string route, TestMessage message)
    {
        var envelope = CreateEnvelope(target, route, message);
        _outbox.Send(envelope);
        await WriteStateAsync();
        return envelope.Envelope.MessageId;
    }

    public async Task<MessageId> SendDuplicateAsync(GrainId target, string route, TestMessage message)
    {
        var envelope = CreateEnvelope(target, route, message);
        _outbox.Send(envelope);
        _outbox.Send(envelope);
        await WriteStateAsync();
        return envelope.Envelope.MessageId;
    }

    public async Task<MessageId> SendAndDeactivateAsync(GrainId target, string route, TestMessage message)
    {
        var messageId = await SendAsync(target, route, message);
        DeactivateOnIdle();
        return messageId;
    }

    public Task<MessageId> StageWithoutCommitAsync(GrainId target, string route, TestMessage message)
    {
        var envelope = CreateEnvelope(target, route, message);
        _outbox.Send(envelope);
        return Task.FromResult(envelope.Envelope.MessageId);
    }

    public async Task RetryWriteStateAsync() => await WriteStateAsync();

    public async Task<DeliveryResult> AcceptAndDeactivateAsync(InboxMessage envelope)
    {
        var extension = _inboxState;
        var result = await extension.DeliverAsync(envelope);
        AcceptedSnapshot = CreateSnapshot();
        DeactivateOnIdle();
        return result;
    }

    public Task StageOutputAsync(OutboxMessage envelope)
    {
        _outbox.Send(envelope);
        return Task.CompletedTask;
    }

    public Task StageEffectAsync(MessageEffect effect)
    {
        _effects[effect.LogicalId] = effect;
        return Task.CompletedTask;
    }

    public Task ConfigureHandlerAsync(bool enabled)
    {
        _handlerConfiguration.Set(this.GetGrainId(), enabled);
        if (enabled && !_handlerRegistered) RegisterHandler();
        return Task.CompletedTask;
    }

    private void RegisterHandler()
    {
        _inbox.Register(new ApplicationDispatcher(this));
        _handlerRegistered = true;
    }

    public async Task<bool> RemoveInboxDeadLetterAsync(MessageId messageId)
    {
        if (!_diagnostics.RemoveInboxDeadLetter(messageId))
        {
            return false;
        }

        await WriteStateAsync();
        return true;
    }

    public Task<EndpointSnapshot> GetSnapshotAsync() => Task.FromResult(CreateSnapshot());

    internal EndpointSnapshot GetSnapshotForTest() => CreateSnapshot();

    public Task RequestDeactivationAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public async Task DeleteStateAndDeactivateAsync()
    {
        try
        {
            await ((ILifecycleObserver)ServiceProvider.GetRequiredService<MessagingPump>()).OnStop(CancellationToken.None);
            if (_outbox is ILifecycleObserver testOutbox)
            {
                await testOutbox.OnStop(CancellationToken.None);
            }

            await _journalOwner.DeleteStateAsync(CancellationToken.None);
        }
        finally
        {
            DeactivateOnIdle();
        }
    }

    internal EndpointSnapshot? AcceptedSnapshot { get; private set; }

    private InboxMessage? _controlEnvelope;
    internal TaskCompletionSource ControlDeliveryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task SetControlEnvelopeAsync(InboxMessage envelope)
    {
        _controlEnvelope = envelope;
        return Task.CompletedTask;
    }

    internal TaskScheduler? JobScheduler { get; private set; }
    internal IGrainContext? JobGrainContext { get; private set; }

    public async Task ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken)
    {
        JobScheduler = TaskScheduler.Current;
        JobGrainContext = ReceiverTestServices.CurrentGrainContext;
        switch (context.Job.Name)
        {
            case "test/deliver-envelope":
                ControlDeliveryEntered.TrySetResult();
                var extension = _inboxState;
                await extension.DeliverAsync(_controlEnvelope ?? throw new InvalidOperationException("A control envelope must be configured."), attemptCancellationToken);
                break;
            case "test/write-journal":
                await StateManager.WriteStateAsync(attemptCancellationToken);
                break;
            case "test/probe-scheduler":
                break;
            default:
                throw new NotSupportedException($"Unknown test job '{context.Job.Name}'.");
        }
    }

    public Task HoldPumpTurnAsync(string barrierRoute, bool deactivate) =>
        HoldPumpTurnCoreAsync(barrierRoute, null, deactivate);

    public Task HoldPumpTurnAsync(string barrierRoute, InboxMessage replacement, bool deactivate) =>
        HoldPumpTurnCoreAsync(barrierRoute, replacement, deactivate);

    private async Task HoldPumpTurnCoreAsync(string barrierRoute, InboxMessage? replacement, bool deactivate)
    {
        if (!_handlerProbe.TryGet(this.GetGrainId(), barrierRoute, out var barrier))
        {
            throw new InvalidOperationException("The pump-turn barrier must be armed.");
        }
        barrier.Entered.TrySetResult();
        await barrier.Continue.Task;
        if (replacement is { } envelope)
        {
            var extension = _inboxState;
            await extension.DeliverAsync(envelope);
        }
        if (deactivate)
        {
            DeactivateOnIdle();
        }
    }

    internal TaskCompletionSource<Exception> DeactivationFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    void IObserver<GrainLifecycleEvents.LifecycleEvent>.OnNext(GrainLifecycleEvents.LifecycleEvent value)
    {
        if (value is GrainLifecycleEvents.Deactivating { Reason.Exception: { } exception } deactivating
            && ReferenceEquals(deactivating.GrainContext, _grainContext))
        {
            DeactivationFailure.TrySetResult(exception);
        }
    }

    void IObserver<GrainLifecycleEvents.LifecycleEvent>.OnError(Exception error) => DeactivationFailure.TrySetException(error);
    void IObserver<GrainLifecycleEvents.LifecycleEvent>.OnCompleted() { }
    public void Dispose()
    {
        _controlEnvelope = null;
        _lifecycleSubscription.Dispose();
    }

    private readonly ConcurrentQueue<EndpointSnapshot> _captures = new();
    private readonly ConcurrentQueue<MessageId[]> _outputCaptures = new();
    internal IReadOnlyList<EndpointSnapshot> Captures => _captures.ToArray();
    internal IReadOnlyList<MessageId[]> OutputCaptures => _outputCaptures.ToArray();
    internal Exception? NextApplyFailure { get; set; }
    internal TaskCompletionSource ApplyAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void ClearCaptures() => _captures.Clear();

    internal EndpointSnapshot CaptureStorageWrite()
    {
        var snapshot = CreateSnapshot();
        _captures.Enqueue(snapshot);
        _outputCaptures.Enqueue(_outbox.Messages().Select(static envelope => envelope.Envelope.MessageId).ToArray());
        return snapshot;
    }

    internal void PublishStoredSnapshot(EndpointSnapshot snapshot) => _snapshotProbe.Publish(this.GetGrainId(), snapshot);
    internal void CaptureStorageRead() => ReplayedSnapshot = CreateSnapshot();
    internal EndpointSnapshot? ReplayedSnapshot { get; private set; }

    private OutboxMessage CreateEnvelope(GrainId target, string route, TestMessage message) =>
        TestApplicationProtocol.Create(_sessions, this.GetGrainId(), target, route, message);

    private async ValueTask HandleAsync(
        TestMessage message,
        IInboxHandlerContext context,
        CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _activeHandlers);
        _maxConcurrentHandlers = Math.Max(_maxConcurrentHandlers, active);
        try
        {
            if (_handlerProbe.TryGet(this.GetGrainId(), TestApplicationProtocol.Read(_sessions, context.Message).Route, out var gate))
            {
                gate.Entered.TrySetResult();
                await gate.WaitAsync(cancellationToken);
            }

            if (_handlerProbe.TryGet(this.GetGrainId(), TestApplicationProtocol.Read(_sessions, context.Message).Route + "/application-preparation", out var preparation))
            {
                preparation.Entered.TrySetResult();
                await preparation.Continue.Task.WaitAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (message.CommitDuringHandling)
            {
                await WriteStateAsync(cancellationToken);
            }
            if (message.ThrowDuringPreparation
                || (message.ThrowOnceDuringPreparation && FailedOnce.TryAdd(message.LogicalId, 0))
                || (message.ThrowForAttempts > 0 && FailedAttempts.AddOrUpdate(message.LogicalId, 1, static (_, count) => count + 1) <= message.ThrowForAttempts))
            {
                throw new InvalidOperationException($"Injected handler preparation failure for {message.LogicalId}.");
            }

            var outgoing = message.ForwardTo is { } destination
                ? TestApplicationProtocol.Create(_sessions, this.GetGrainId(), destination, "messages/forwarded",
                    message with { ForwardTo = null, ThrowDuringPreparation = false },
                    new MessageId(context.Message.Envelope.MessageId.Key.CreateChildKey("forwarded")))
                : (OutboxMessage?)null;
            if (NextApplyFailure is { } failure)
            {
                NextApplyFailure = null;
                throw failure;
            }
            _effects.TryGetValue(message.LogicalId, out var prior);
            _effects[message.LogicalId] = new MessageEffect(message.LogicalId, (prior?.Count ?? 0) + 1, message.Sequence, message.Value);
            ApplyAttempted.TrySetResult();
            if (outgoing is { } output)
            {
                _outbox.Send(output);
                if (TestApplicationProtocol.Read(_sessions, context.Message).Route == "messages/duplicate-output")
                {
                    _outbox.Send(output);
                }
            }
            context.Complete();
        }
        finally
        {
            Interlocked.Decrement(ref _activeHandlers);
        }
    }

    private void PublishSnapshot() => _snapshotProbe.Publish(this.GetGrainId(), CreateSnapshot());

    private EndpointSnapshot CreateSnapshot() =>
        new(
            _activationId,
            _siloDetails.SiloAddress.ToParsableString(),
            _inboxState.Count,
            _outbox.Count(),
            _maxConcurrentHandlers,
            _effects.Values.OrderBy(static effect => effect.Sequence).ToArray(),
            _diagnostics.InboxDeadLetters.Select(ToSnapshot).ToArray(),
            [],
            InboxTestAccess.GetProcessedCount(_inboxState));

    private DeadLetterSnapshot ToSnapshot(DeadLetter<InboxMessage> deadLetter) =>
        new(
            deadLetter.Message.Envelope.MessageId,
            TestApplicationProtocol.Read(_sessions, deadLetter.Message).Route,
            deadLetter.Reason,
            deadLetter.AttemptCount,
            deadLetter.DeadLetteredAt);

    private sealed class ApplicationDispatcher(MessagingTestGrain owner) : IInboxHandler
    {
        public bool CanHandle(InboxMessage message) => true;

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            if (owner.HandlerOverride is { } handler)
            {
                return handler.HandleAsync(context, cancellationToken);
            }

            var application = TestApplicationProtocol.Read(owner._sessions, context.Message);
            if (!application.Route.StartsWith("messages/", StringComparison.Ordinal) && application.Route != "typed")
            {
                throw new InvalidOperationException($"Unknown application route '{application.Route}'.");
            }
            return owner.HandleAsync(application.Body as TestMessage
                ?? throw new InvalidOperationException($"Expected {nameof(TestMessage)} application payload."),
                context, cancellationToken);
        }
    }
}

// Tests can change registration between real activations without adding a production registry API.
public sealed class TestHandlerConfiguration
{
    private readonly ConcurrentDictionary<GrainId, bool> _enabled = new();
    public bool IsEnabled(GrainId id) => !_enabled.TryGetValue(id, out var enabled) || enabled;
    public void Set(GrainId id, bool enabled) => _enabled[id] = enabled;
}
