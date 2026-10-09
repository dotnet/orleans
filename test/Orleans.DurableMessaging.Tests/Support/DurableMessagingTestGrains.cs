using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.DurableMessaging;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Serialization;
using Orleans.Serialization.Session;

namespace Orleans.DurableMessaging.Tests.Support;

public interface IDurableMessagingTestGrain : IGrainWithGuidKey
{
    Task<HierarchicalKey> SendAsync(GrainId target, string route, DurableTestMessage message);
    Task<HierarchicalKey> SendDuplicateAsync(GrainId target, string route, DurableTestMessage message);
    Task<HierarchicalKey> SendAndDeactivateAsync(GrainId target, string route, DurableTestMessage message);
    Task<HierarchicalKey> StageWithoutCommitAsync(GrainId target, string route, DurableTestMessage message);
    Task RetryWriteStateAsync();
    Task StageEffectAsync(DurableEffect effect);
    Task StageOutputAsync([DisposeOnCompletion] DurableEnvelope envelope);
    Task<DeliveryResult> AcceptAndDeactivateAsync([DisposeOnCompletion] DurableEnvelope envelope);
    Task SetInboxOwnershipAsync(string ownershipId, DurableJob job);
    Task SeedInboxStateAsync([DisposeOnCompletion] DurableEnvelope envelope, string? ownershipId, DurableJob? job);
    Task ConfigureHandlerAsync(bool enabled);
    Task<bool> RemoveInboxDeadLetterAsync(HierarchicalKey messageId);
    Task<bool> RemoveOutboxDeadLetterAsync(HierarchicalKey messageId);
    Task<DurableEndpointSnapshot> GetSnapshotAsync();
    Task RequestDeactivationAsync();
    Task SetControlEnvelopeAsync([DisposeOnCompletion] DurableEnvelope envelope);
    Task DeleteStateAndDeactivateAsync();
    Task HoldPumpTurnAsync(string barrierRoute, bool deactivate);
    Task HoldPumpTurnAsync(string barrierRoute, [DisposeOnCompletion] DurableEnvelope replacement, bool deactivate);
}

[GenerateSerializer, Immutable]
public sealed record DurableTestMessage(
    [property: Id(0)] HierarchicalKey LogicalId,
    [property: Id(1)] int Sequence,
    [property: Id(2)] string Value,
    [property: Id(3)] GrainId? ForwardTo = null,
    [property: Id(8)] bool ThrowDuringPreparation = false,
    [property: Id(5)] bool CommitDuringHandling = false,
    [property: Id(6)] bool DeleteDuringHandling = false,
    [property: Id(9)] bool ThrowOnceDuringPreparation = false);

[GenerateSerializer, Immutable]
public sealed record DurableEffect(
    [property: Id(0)] HierarchicalKey LogicalId,
    [property: Id(1)] int Count,
    [property: Id(2)] int Sequence,
    [property: Id(3)] string Value);

[GenerateSerializer, Immutable]
public sealed record DurableEndpointSnapshot(
    [property: Id(0)] Guid ActivationId,
    [property: Id(1)] string SiloAddress,
    [property: Id(2)] int InboxCount,
    [property: Id(3)] int OutboxCount,
    [property: Id(4)] int MaxConcurrentHandlers,
    [property: Id(5)] IReadOnlyList<DurableEffect> Effects,
    [property: Id(6)] IReadOnlyList<DurableDeadLetterSnapshot> InboxDeadLetters,
    [property: Id(7)] IReadOnlyList<DurableDeadLetterSnapshot> OutboxDeadLetters,
    [property: Id(8)] string? InboxJobId,
    [property: Id(9)] int ProcessedMessageCount,
    [property: Id(12)] string? OutboxJobId,
    [property: Id(16)] DurableJob? InboxJob,
    [property: Id(17)] DurableJob? OutboxJob);

[GenerateSerializer, Immutable]
public sealed record DurableDeadLetterSnapshot(
    [property: Id(0)] HierarchicalKey MessageId,
    [property: Id(1)] string Route,
    [property: Id(2)] string Reason,
    [property: Id(3)] int AttemptCount,
    [property: Id(4)] DateTimeOffset DeadLetteredAt);

[GrainType("durable-messaging-inbox-test")]
public sealed class DurableMessagingTestGrain : DurableGrain, IDurableMessagingTestGrain, IDurableJobHandler,
    IObserver<GrainLifecycleEvents.LifecycleEvent>, IDisposable
{
    private readonly IGrainContext _grainContext;
    private readonly IDisposable _lifecycleSubscription;
    private readonly IJournaledStateManager _journalOwner;
    private readonly IDurableInbox _inbox;
    private readonly IDurableOutbox _outbox;
    private readonly IDurableMessagingDiagnostics _diagnostics;
    private readonly IDurableDictionary<HierarchicalKey, DurableEffect> _effects;
    private readonly IDurableDictionary<HierarchicalKey, DateTimeOffset> _processedMessages;
    private readonly SerializerSessionPool _sessions;
    private readonly TestHandlerConfiguration _handlerConfiguration;
    internal IInboxHandler? HandlerOverride { get; set; }
    private readonly IDurableValue<string> _inboxJobId;
    private readonly IDurableValue<DurableJob> _inboxJob;
    private readonly IDurableValue<string> _outboxJobId;
    private readonly IDurableValue<DurableJob> _outboxJob;
    private readonly ILocalSiloDetails _siloDetails;
    private readonly HandlerProbe _handlerProbe;
    private readonly SnapshotProbe _snapshotProbe;
    private readonly Guid _activationId = Guid.NewGuid();
    private int _activeHandlers;
    private int _maxConcurrentHandlers;
    private readonly HashSet<HierarchicalKey> _failedOnce = [];

    public DurableMessagingTestGrain(
        IGrainContext grainContext,
        IJournaledStateManager journalOwner,
        IDurableInbox inbox,
        IDurableOutbox outbox,
        IDurableMessagingDiagnostics diagnostics,
        [FromKeyedServices("test-effects")] IDurableDictionary<HierarchicalKey, DurableEffect> effects,
        [FromKeyedServices("inbox")] IDurableValue<string> applicationInboxState,
        [FromKeyedServices("__orleans.durable-messaging.inbox-processed")] IDurableDictionary<HierarchicalKey, DateTimeOffset> processedMessages,
        [FromKeyedServices("__orleans.durable-messaging.inbox-job-id")] IDurableValue<string> inboxJobId,
        [FromKeyedServices("__orleans.durable-messaging.inbox-job-handle")] IDurableValue<DurableJob> inboxJob,
        [FromKeyedServices("__orleans.durable-messaging.outbox-job-id")] IDurableValue<string> outboxJobId,
        [FromKeyedServices("__orleans.durable-messaging.outbox-job-handle")] IDurableValue<DurableJob> outboxJob,
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
        _processedMessages = processedMessages;
        _inboxJobId = inboxJobId;
        _inboxJob = inboxJob;
        _outboxJobId = outboxJobId;
        _outboxJob = outboxJob;
        _sessions = sessions;
        _handlerConfiguration = handlerConfiguration;
        _siloDetails = siloDetails;
        _handlerProbe = handlerProbe;
        _snapshotProbe = snapshotProbe;
        _lifecycleSubscription = GrainLifecycleEvents.AllEvents.Subscribe(this);
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (_handlerConfiguration.IsEnabled(this.GetGrainId()))
        {
            _inbox.RegisterHandler(new ApplicationDispatcher(this));
        }
        await base.OnActivateAsync(cancellationToken);
        _snapshotProbe.Publish(this.GetGrainId(), CreateSnapshot());
    }

    public async Task<HierarchicalKey> SendAsync(GrainId target, string route, DurableTestMessage message)
    {
        using var envelope = CreateEnvelope(target, route, message);
        _outbox.Send(envelope);
        await WriteStateAsync();
        return envelope.MessageId;
    }

    public async Task<HierarchicalKey> SendDuplicateAsync(GrainId target, string route, DurableTestMessage message)
    {
        using var envelope = CreateEnvelope(target, route, message);
        _outbox.Send(envelope);
        _outbox.Send(envelope);
        await WriteStateAsync();
        return envelope.MessageId;
    }

    public async Task<HierarchicalKey> SendAndDeactivateAsync(GrainId target, string route, DurableTestMessage message)
    {
        var messageId = await SendAsync(target, route, message);
        DeactivateOnIdle();
        return messageId;
    }

    public Task<HierarchicalKey> StageWithoutCommitAsync(GrainId target, string route, DurableTestMessage message)
    {
        using var envelope = CreateEnvelope(target, route, message);
        _outbox.Send(envelope);
        return Task.FromResult(envelope.MessageId);
    }

    public async Task RetryWriteStateAsync() => await WriteStateAsync();

    public async Task<DeliveryResult> AcceptAndDeactivateAsync(DurableEnvelope envelope)
    {
        var extension = (IDurableInboxExtension)ServiceProvider.GetRequiredKeyedService<IGrainExtension>(typeof(IDurableInboxExtension));
        var result = await extension.DeliverAsync(envelope);
        AcceptedSnapshot = CreateSnapshot();
        DeactivateOnIdle();
        return result;
    }

    public Task StageOutputAsync(DurableEnvelope envelope)
    {
        _outbox.Send(envelope);
        return Task.CompletedTask;
    }

    public Task StageEffectAsync(DurableEffect effect)
    {
        _effects[effect.LogicalId] = effect;
        return Task.CompletedTask;
    }

    public async Task SetInboxOwnershipAsync(string ownershipId, DurableJob job)
    {
        _inboxJobId.Value = ownershipId;
        _inboxJob.Value = job;
        await WriteStateAsync();
    }

    public async Task SeedInboxStateAsync(DurableEnvelope envelope, string? ownershipId, DurableJob? job)
    {
        var messages = ServiceProvider.GetRequiredKeyedService<IDurableDictionary<HierarchicalKey, DurableEnvelope>>(
            "__orleans.durable-messaging.inbox");
        messages.Add(envelope.MessageId, envelope);
        _inboxJobId.Value = ownershipId;
        _inboxJob.Value = job;
        await WriteStateAsync();
    }

    public Task ConfigureHandlerAsync(bool enabled)
    {
        _handlerConfiguration.Set(this.GetGrainId(), enabled);
        return Task.CompletedTask;
    }

    public async Task<bool> RemoveInboxDeadLetterAsync(HierarchicalKey messageId)
    {
        if (!_diagnostics.RemoveInboxDeadLetter(messageId))
        {
            return false;
        }

        await WriteStateAsync();
        return true;
    }

    public async Task<bool> RemoveOutboxDeadLetterAsync(HierarchicalKey messageId)
    {
        if (!_diagnostics.RemoveOutboxDeadLetter(messageId))
        {
            return false;
        }

        await WriteStateAsync();
        return true;
    }

    public Task<DurableEndpointSnapshot> GetSnapshotAsync() => Task.FromResult(CreateSnapshot());

    internal DurableEndpointSnapshot GetSnapshotForTest() => CreateSnapshot();

    public Task RequestDeactivationAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public async Task DeleteStateAndDeactivateAsync()
    {
        try
        {
            var inbox = (ILifecycleObserver)ServiceProvider.GetRequiredKeyedService<IGrainExtension>(typeof(IDurableInboxExtension));
            var outbox = (ILifecycleObserver)_outbox;
            await Task.WhenAll(inbox.OnStop(CancellationToken.None), outbox.OnStop(CancellationToken.None));
            await _journalOwner.DeleteStateAsync(CancellationToken.None);
        }
        finally
        {
            DeactivateOnIdle();
        }
    }

    internal DurableEndpointSnapshot? AcceptedSnapshot { get; private set; }

    private DurableEnvelope? _controlEnvelope;
    internal TaskCompletionSource ControlDeliveryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task SetControlEnvelopeAsync(DurableEnvelope envelope)
    {
        var retained = envelope.Retain();
        _controlEnvelope?.Dispose();
        _controlEnvelope = retained;
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
                var extension = (IDurableInboxExtension)ServiceProvider.GetRequiredKeyedService<IGrainExtension>(typeof(IDurableInboxExtension));
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

    public Task HoldPumpTurnAsync(string barrierRoute, DurableEnvelope replacement, bool deactivate) =>
        HoldPumpTurnCoreAsync(barrierRoute, replacement, deactivate);

    private async Task HoldPumpTurnCoreAsync(string barrierRoute, DurableEnvelope? replacement, bool deactivate)
    {
        if (!_handlerProbe.TryGet(this.GetGrainId(), barrierRoute, out var barrier))
        {
            throw new InvalidOperationException("The pump-turn barrier must be armed.");
        }
        barrier.Entered.TrySetResult();
        await barrier.Continue.Task;
        if (replacement is { } envelope)
        {
            var extension = (IDurableInboxExtension)ServiceProvider.GetRequiredKeyedService<IGrainExtension>(typeof(IDurableInboxExtension));
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
        _controlEnvelope?.Dispose();
        _controlEnvelope = null;
        _lifecycleSubscription.Dispose();
    }

    private readonly ConcurrentQueue<DurableEndpointSnapshot> _captures = new();
    private readonly ConcurrentQueue<HierarchicalKey[]> _outputCaptures = new();
    internal IReadOnlyList<DurableEndpointSnapshot> Captures => _captures.ToArray();
    internal IReadOnlyList<HierarchicalKey[]> OutputCaptures => _outputCaptures.ToArray();
    internal Exception? NextApplyFailure { get; set; }
    internal TaskCompletionSource ApplyAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void ClearCaptures() => _captures.Clear();

    internal DurableEndpointSnapshot CaptureStorageWrite()
    {
        var snapshot = CreateSnapshot();
        _captures.Enqueue(snapshot);
        _outputCaptures.Enqueue(_outbox.Messages.Select(static envelope => envelope.MessageId).ToArray());
        return snapshot;
    }

    internal void PublishStoredSnapshot(DurableEndpointSnapshot snapshot) => _snapshotProbe.Publish(this.GetGrainId(), snapshot);
    internal void CaptureStorageRead() => ReplayedSnapshot = CreateSnapshot();
    internal DurableEndpointSnapshot? ReplayedSnapshot { get; private set; }

    private DurableEnvelope CreateEnvelope(GrainId target, string route, DurableTestMessage message) =>
        TestApplicationProtocol.Create(_sessions, this.GetGrainId(), target, route, message);

    private async ValueTask HandleAsync(
        DurableTestMessage message,
        IInboxHandlerContext context,
        CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _activeHandlers);
        _maxConcurrentHandlers = Math.Max(_maxConcurrentHandlers, active);
        try
        {
            if (_handlerProbe.TryGet(this.GetGrainId(), TestApplicationProtocol.Read(_sessions, context.Envelope).Route, out var gate))
            {
                gate.Entered.TrySetResult();
                await gate.Continue.Task.WaitAsync(cancellationToken);
            }

            if (_handlerProbe.TryGet(this.GetGrainId(), TestApplicationProtocol.Read(_sessions, context.Envelope).Route + "/application-preparation", out var preparation))
            {
                preparation.Entered.TrySetResult();
                await preparation.Continue.Task.WaitAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (message.CommitDuringHandling)
            {
                await WriteStateAsync(cancellationToken);
            }
            if (message.ThrowDuringPreparation || (message.ThrowOnceDuringPreparation && _failedOnce.Add(message.LogicalId)))
            {
                throw new InvalidOperationException($"Injected handler preparation failure for {message.LogicalId}.");
            }

            using var outgoing = message.ForwardTo is { } destination
                ? TestApplicationProtocol.Create(_sessions, this.GetGrainId(), destination, "messages/forwarded",
                    message with { ForwardTo = null, ThrowDuringPreparation = false },
                    context.Envelope.MessageId.CreateChildKey("forwarded"))
                : (DurableEnvelope?)null;
            if (NextApplyFailure is { } failure)
            {
                NextApplyFailure = null;
                throw failure;
            }
            _effects.TryGetValue(message.LogicalId, out var prior);
            _effects[message.LogicalId] = new DurableEffect(message.LogicalId, (prior?.Count ?? 0) + 1, message.Sequence, message.Value);
            ApplyAttempted.TrySetResult();
            if (outgoing is { } output)
            {
                _outbox.Send(output);
                if (TestApplicationProtocol.Read(_sessions, context.Envelope).Route == "messages/duplicate-output")
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

    private DurableEndpointSnapshot CreateSnapshot() =>
        new(
            _activationId,
            _siloDetails.SiloAddress.ToParsableString(),
            _inbox.Count,
            _outbox.Count,
            _maxConcurrentHandlers,
            _effects.Values.OrderBy(static effect => effect.Sequence).ToArray(),
            _diagnostics.InboxDeadLetters.Select(ToSnapshot).ToArray(),
            _diagnostics.OutboxDeadLetters.Select(ToSnapshot).ToArray(),
            _inboxJobId.Value,
            _processedMessages.Count,
            _outboxJobId.Value,
            _inboxJob.Value,
            _outboxJob.Value);

    private DurableDeadLetterSnapshot ToSnapshot(DurableDeadLetter deadLetter) =>
        new(
            deadLetter.Message.MessageId,
            TestApplicationProtocol.Read(_sessions, deadLetter.Message).Route,
            deadLetter.Reason,
            deadLetter.AttemptCount,
            deadLetter.DeadLetteredAt);

    private sealed class ApplicationDispatcher(DurableMessagingTestGrain owner) : IInboxHandler
    {
        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            if (owner.HandlerOverride is { } handler)
            {
                return handler.HandleAsync(context, cancellationToken);
            }

            var application = TestApplicationProtocol.Read(owner._sessions, context.Envelope);
            if (!application.Route.StartsWith("messages/", StringComparison.Ordinal) && application.Route != "typed")
            {
                throw new InvalidOperationException($"Unknown application route '{application.Route}'.");
            }
            return owner.HandleAsync(application.Body as DurableTestMessage
                ?? throw new InvalidOperationException($"Expected {nameof(DurableTestMessage)} application payload."),
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
