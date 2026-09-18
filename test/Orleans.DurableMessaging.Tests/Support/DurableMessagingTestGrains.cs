using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.DurableMessaging;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.DurableMessaging.Tests.Support;

public interface IDurableMessagingTestGrain : IGrainWithGuidKey
{
    Task RetryWriteStateAsync();
    Task StageEffectAsync(DurableEffect effect);
    Task StageOutputAsync(DurableEnvelope envelope);
    Task<DeliveryResult> AcceptAndDeactivateAsync(DurableEnvelope envelope);
    Task SetInboxOwnershipAsync(string ownershipId, DurableJob job);
    Task SeedInboxStateAsync(DurableEnvelope envelope, string? ownershipId, DurableJob? job);
    Task<DuplicateRouteRegistrationResult> RegisterDuplicateExactRouteHandlersAsync(string route);
    Task<RouteLookupValidationResult> ValidateRouteLookupAsync(string? route);
    Task<bool> RemoveInboxDeadLetterAsync(GrainId senderId, Guid messageId);
    Task<DurableEndpointSnapshot> GetSnapshotAsync();
    Task RequestDeactivationAsync();
    Task SetControlEnvelopeAsync(DurableEnvelope envelope);
    Task<DeliveryResult> DeleteJournalThenDeliverAsync(DurableEnvelope envelope);
    Task HoldPumpTurnAsync(string barrierRoute, DurableEnvelope? replacement, bool deactivate);
}

[GenerateSerializer, Immutable]
public sealed record DuplicateRouteRegistrationResult(
    [property: Id(0)] string ExceptionMessage,
    [property: Id(1)] bool LookupRetainedFirstHandler);

[GenerateSerializer, Immutable]
public sealed record RouteLookupValidationResult(
    [property: Id(0)] string HasHandlerParameterName,
    [property: Id(1)] string TryGetHandlerParameterName);

[GenerateSerializer, Immutable]
public sealed record DurableTestMessage(
    [property: Id(0)] Guid LogicalId,
    [property: Id(1)] int Sequence,
    [property: Id(2)] string Value,
    [property: Id(3)] GrainId? ForwardTo = null,
    [property: Id(8)] bool ThrowDuringPreparation = false,
    [property: Id(5)] bool CommitDuringHandling = false,
    [property: Id(6)] bool DeleteDuringHandling = false,
    [property: Id(9)] bool ThrowOnceDuringPreparation = false);

[GenerateSerializer, Immutable]
public sealed record DurableEffect(
    [property: Id(0)] Guid LogicalId,
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
    [property: Id(10)] int FirstExactRouteHandlerCalls,
    [property: Id(11)] int ReplacementExactRouteHandlerCalls,
    [property: Id(12)] string? OutboxJobId,
    [property: Id(13)] int NullReferenceMessageCalls,
    [property: Id(14)] int NullNullableValueMessageCalls,
    [property: Id(15)] int GenericExactRouteHandlerCalls,
    [property: Id(16)] DurableJob? InboxJob,
    [property: Id(17)] DurableJob? OutboxJob);

[GenerateSerializer, Immutable]
public sealed record DurableDeadLetterSnapshot(
    [property: Id(0)] Guid MessageId,
    [property: Id(1)] string Route,
    [property: Id(2)] string Reason,
    [property: Id(3)] int AttemptCount,
    [property: Id(4)] DateTimeOffset DeadLetteredAt);

[GrainType("durable-messaging-inbox-test")]
public sealed class DurableMessagingTestGrain : DurableGrain, IDurableMessagingTestGrain, IDurableJobHandler
{
    private readonly IDurableInbox _inbox;
    private readonly IDurableOutbox _outbox;
    private readonly IDurableMessagingDiagnostics _diagnostics;
    private readonly IDurableDictionary<Guid, DurableEffect> _effects;
    private readonly IDurableDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> _processedMessages;
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
    private int _firstExactRouteHandlerCalls;
    private int _replacementExactRouteHandlerCalls;
    private int _genericExactRouteHandlerCalls;
    private int _nullReferenceMessageCalls;
    private int _nullNullableValueMessageCalls;
    private int _handlerSelectionCalls;
    private int _mutatingSelectionCalls;
    private readonly HashSet<Guid> _failedOnce = [];

    public DurableMessagingTestGrain(
        IDurableInbox inbox,
        IDurableOutbox outbox,
        IDurableMessagingDiagnostics diagnostics,
        [FromKeyedServices("test-effects")] IDurableDictionary<Guid, DurableEffect> effects,
        [FromKeyedServices("inbox")] IDurableValue<string> applicationInboxState,
        [FromKeyedServices("__orleans.durable-messaging.inbox-processed")] IDurableDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> processedMessages,
        [FromKeyedServices("__orleans.durable-messaging.inbox-job-id")] IDurableValue<string> inboxJobId,
        [FromKeyedServices("__orleans.durable-messaging.inbox-job-handle")] IDurableValue<DurableJob> inboxJob,
        [FromKeyedServices("__orleans.durable-messaging.outbox-job-id")] IDurableValue<string> outboxJobId,
        [FromKeyedServices("__orleans.durable-messaging.outbox-job-handle")] IDurableValue<DurableJob> outboxJob,
        ILocalSiloDetails siloDetails,
        HandlerProbe handlerProbe,
        SnapshotProbe snapshotProbe)
    {
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
        _siloDetails = siloDetails;
        _handlerProbe = handlerProbe;
        _snapshotProbe = snapshotProbe;
        var journal = (ObservedJournalDictionary<Guid, DurableEffect>)effects;
        journal.ValidateWriting = OnWriteRequested;
        journal.ValidateDeleting = OnDeleteRequested;
        journal.Capturing = OnWriteStarted;
        journal.Written = OnWriteCompleted;
        journal.Recovered = OnRecoveryCompleted;
        journal.Faulted = OnFaulted;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _inbox.RegisterHandler(new ThrowingSelectionHandler(this));
        _inbox.RegisterHandler(new MutatingSelectionHandler(this));
        _inbox.RegisterHandler("nullable/reference", new NullReferenceMessageHandler(this));
        _inbox.RegisterHandler("nullable/value", new NullNullableValueMessageHandler(this));
        _inbox.RegisterHandler(new TypedMessageHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task RetryWriteStateAsync() => await WriteStateAsync();

    public async Task<DeliveryResult> AcceptAndDeactivateAsync(DurableEnvelope envelope)
    {
        var extension = (IDurableInboxExtension)ServiceProvider.GetRequiredKeyedService<IGrainExtension>(typeof(IDurableInboxExtension));
        var result = await extension.DeliverAsync(envelope);
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
        var messages = ServiceProvider.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>(
            "__orleans.durable-messaging.inbox");
        messages.Add((envelope.SenderId, envelope.MessageId), envelope);
        _inboxJobId.Value = ownershipId;
        _inboxJob.Value = job;
        await WriteStateAsync();
    }

    public Task<DuplicateRouteRegistrationResult> RegisterDuplicateExactRouteHandlersAsync(string route)
    {
        var first = new CountingHandler(() => _firstExactRouteHandlerCalls++);
        var replacement = new CountingHandler(() => _replacementExactRouteHandlerCalls++);
        _inbox.RegisterHandler(new RouteSpecificCountingHandler(route, () => _genericExactRouteHandlerCalls++));
        _inbox.RegisterHandler(route, first);
        var exception = GetDuplicateRegistrationException(route, replacement);
        var retained = _inbox.TryGetHandler(route, out var cached) && ReferenceEquals(first, cached);

        return Task.FromResult(new DuplicateRouteRegistrationResult(exception.Message, retained));
    }

    public Task<RouteLookupValidationResult> ValidateRouteLookupAsync(string? route)
    {
        var hasHandlerParameterName = GetRouteLookupExceptionParameterName(() => _inbox.HasHandler(route!));
        var tryGetHandlerParameterName = GetRouteLookupExceptionParameterName(() => _inbox.TryGetHandler(route!, out _));
        return Task.FromResult(new RouteLookupValidationResult(
            hasHandlerParameterName,
            tryGetHandlerParameterName));
    }

    public async Task<bool> RemoveInboxDeadLetterAsync(GrainId senderId, Guid messageId)
    {
        if (!_diagnostics.RemoveInboxDeadLetter(senderId, messageId))
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

    public async Task<DeliveryResult> DeleteJournalThenDeliverAsync(DurableEnvelope envelope)
    {
        await StateManager.DeleteStateAsync(CancellationToken.None);
        var extension = (IDurableInboxExtension)ServiceProvider.GetRequiredKeyedService<IGrainExtension>(typeof(IDurableInboxExtension));
        return await extension.DeliverAsync(envelope);
    }

    private DurableEnvelope? _controlEnvelope;
    internal TaskCompletionSource ControlDeliveryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task SetControlEnvelopeAsync(DurableEnvelope envelope)
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
                var extension = (IDurableInboxExtension)ServiceProvider.GetRequiredKeyedService<IGrainExtension>(typeof(IDurableInboxExtension));
                await extension.DeliverAsync(_controlEnvelope ?? throw new InvalidOperationException("A control envelope must be configured."), attemptCancellationToken);
                break;
            case "test/write-journal":
                await StateManager.WriteStateAsync(attemptCancellationToken);
                break;
            case "test/delete-journal":
                await StateManager.DeleteStateAsync(attemptCancellationToken);
                break;
            case "test/probe-scheduler":
                break;
            default:
                throw new NotSupportedException($"Unknown test job '{context.Job.Name}'.");
        }
    }

    public async Task HoldPumpTurnAsync(string barrierRoute, DurableEnvelope? replacement, bool deactivate)
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

    internal Exception? NextWriteRejection { get; set; }
    internal Exception? NextDeleteRejection { get; set; }

    public void OnDeleteRequested()
    {
        if (NextDeleteRejection is { } exception)
        {
            NextDeleteRejection = null;
            throw exception;
        }
    }
    public void OnWriteRequested()
    {
        if (NextWriteRejection is { } exception)
        {
            NextWriteRejection = null;
            throw exception;
        }
    }

    internal TaskCompletionSource<Exception> Faulted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void OnFaulted(Exception exception) => Faulted.TrySetResult(exception);

    internal List<DurableEndpointSnapshot> Captures { get; } = [];
    private DurableEndpointSnapshot? _capturedSnapshot;
    internal Exception? NextApplyFailure { get; set; }
    internal TaskCompletionSource ApplyAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void OnWriteStarted() => Captures.Add(_capturedSnapshot = CreateSnapshot());

    public void OnWriteCompleted()
    {
        if (_capturedSnapshot is { } snapshot)
        {
            _snapshotProbe.Publish(this.GetGrainId(), snapshot);
            _capturedSnapshot = null;
        }
    }
    internal DurableEndpointSnapshot? ReplayedSnapshot { get; private set; }

    public void OnRecoveryCompleted()
    {
        ReplayedSnapshot = CreateSnapshot();
        _snapshotProbe.Publish(this.GetGrainId(), ReplayedSnapshot);
    }

    private async ValueTask<Action> PrepareAsync(
        DurableTestMessage message,
        IInboxHandlerContext context,
        CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _activeHandlers);
        _maxConcurrentHandlers = Math.Max(_maxConcurrentHandlers, active);
        try
        {
            if (_handlerProbe.TryGet(this.GetGrainId(), context.Envelope.RouteKey, out var gate))
            {
                gate.Entered.TrySetResult();
                await gate.Continue.Task.WaitAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (message.CommitDuringHandling)
            {
                await WriteStateAsync(cancellationToken);
            }
            if (message.DeleteDuringHandling)
            {
                await StateManager.DeleteStateAsync(cancellationToken);
            }
            if (message.ThrowDuringPreparation || (message.ThrowOnceDuringPreparation && _failedOnce.Add(message.LogicalId)))
            {
                throw new InvalidOperationException($"Injected handler preparation failure for {message.LogicalId}.");
            }

            DurableEnvelope? outgoing = null;
            if (message.ForwardTo is { } target)
            {
                outgoing = context.CreateEnvelope().To(target, "messages/forwarded")
                    .WithBody(message with { ForwardTo = null, ThrowDuringPreparation = false }).Build();
            }
            return () =>
            {
                _effects.TryGetValue(message.LogicalId, out var prior);
                _effects[message.LogicalId] = new DurableEffect(message.LogicalId, (prior?.Count ?? 0) + 1, message.Sequence, message.Value);
                ApplyAttempted.TrySetResult();
                if (NextApplyFailure is { } failure)
                {
                    NextApplyFailure = null;
                    throw failure;
                }
                if (outgoing is { } output)
                {
                    context.Send(output);
                    if (context.Envelope.RouteKey == "messages/duplicate-output")
                    {
                        context.Send(output);
                    }
                }
            };
        }
        finally
        {
            Interlocked.Decrement(ref _activeHandlers);
        }
    }

    private void PublishSnapshot() => _snapshotProbe.Publish(this.GetGrainId(), CreateSnapshot());

    private bool AttemptWriteDuringHandlerSelection()
    {
        try
        {
            WriteStateAsync().GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
        }

        return false;
    }

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
            _firstExactRouteHandlerCalls,
            _replacementExactRouteHandlerCalls,
            _outboxJobId.Value,
            _nullReferenceMessageCalls,
            _nullNullableValueMessageCalls,
            _genericExactRouteHandlerCalls,
            _inboxJob.Value,
            _outboxJob.Value);

    private static DurableDeadLetterSnapshot ToSnapshot(DurableDeadLetter deadLetter) =>
        new(
            deadLetter.Message.MessageId,
            deadLetter.Message.RouteKey,
            deadLetter.Reason,
            deadLetter.AttemptCount,
            deadLetter.DeadLetteredAt);

    private sealed class TypedMessageHandler(DurableMessagingTestGrain owner) : IInboxHandler<DurableTestMessage>
    {
        bool IInboxHandler.CanHandle(IInboxHandlerContext context)
        {
            if (context.Envelope.RouteKey == "messages/can-handle-write")
            {
                return owner.AttemptWriteDuringHandlerSelection();
            }

            return context.Envelope.RouteKey.StartsWith("messages/", StringComparison.Ordinal)
                || context.Envelope.RouteKey == "typed";
        }

        public ValueTask<Action> PrepareAsync(
            DurableTestMessage? message,
            IInboxHandlerContext context,
            CancellationToken cancellationToken) =>
            owner.PrepareAsync(
                message ?? throw new InvalidOperationException("A durable test message is required."),
                context,
                cancellationToken);
    }

    private sealed class ThrowingSelectionHandler(DurableMessagingTestGrain owner) : IInboxHandler<DurableTestMessage>
    {
        public bool CanHandle(IInboxHandlerContext context)
        {
            if (!string.Equals(context.Envelope.RouteKey, "messages/selection-failure", StringComparison.Ordinal))
            {
                return false;
            }

            if (Interlocked.Increment(ref owner._handlerSelectionCalls) == 2)
            {
                throw new InvalidOperationException("Injected handler selection failure.");
            }

            return true;
        }

        public ValueTask<Action> PrepareAsync(
            DurableTestMessage? message,
            IInboxHandlerContext context,
            CancellationToken cancellationToken) =>
            owner.PrepareAsync(
                message ?? throw new InvalidOperationException("A durable test message is required."),
                context,
                cancellationToken);
    }

    private sealed class MutatingSelectionHandler(DurableMessagingTestGrain owner) : IInboxHandler<DurableTestMessage>
    {
        public bool CanHandle(IInboxHandlerContext context)
        {
            if (!string.Equals(context.Envelope.RouteKey, "messages/selection-mutation", StringComparison.Ordinal))
            {
                return false;
            }

            if (Interlocked.Increment(ref owner._mutatingSelectionCalls) > 1)
            {
                var outgoing = context.CreateEnvelope()
                    .To(context.GrainId, "messages/record")
                    .WithBody(new DurableTestMessage(Guid.NewGuid(), 81, "selection-side-effect"))
                    .Build();
                context.Send(outgoing);
                if (context.Envelope.RouteKey == "messages/duplicate-output")
                {
                    context.Send(outgoing);
                }
            }

            return true;
        }

        public ValueTask<Action> PrepareAsync(
            DurableTestMessage? message,
            IInboxHandlerContext context,
            CancellationToken cancellationToken) =>
            owner.PrepareAsync(
                message ?? throw new InvalidOperationException("A durable test message is required."),
                context,
                cancellationToken);
    }

    private sealed class NullReferenceMessageHandler(DurableMessagingTestGrain owner) : IInboxHandler<string?>
    {
        public ValueTask<Action> PrepareAsync(
            string? message,
            IInboxHandlerContext context,
            CancellationToken cancellationToken)
        {
            if (message is not null)
            {
                throw new InvalidOperationException("Expected a null reference message.");
            }

            return ValueTask.FromResult<Action>(() => owner._nullReferenceMessageCalls++);
        }
    }

    private sealed class NullNullableValueMessageHandler(DurableMessagingTestGrain owner) : IInboxHandler<int?>
    {
        public ValueTask<Action> PrepareAsync(
            int? message,
            IInboxHandlerContext context,
            CancellationToken cancellationToken)
        {
            if (message is not null)
            {
                throw new InvalidOperationException("Expected a null nullable value message.");
            }

            return ValueTask.FromResult<Action>(() => owner._nullNullableValueMessageCalls++);
        }
    }

    private sealed class CountingHandler(Action onCall) : IInboxHandler
    {
        public bool CanHandle(IInboxHandlerContext context) => true;

        public ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(onCall);
        }
    }

    private sealed class RouteSpecificCountingHandler(string route, Action onCall) : IInboxHandler
    {
        public bool CanHandle(IInboxHandlerContext context) =>
            string.Equals(context.Envelope.RouteKey, route, StringComparison.Ordinal);

        public ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(onCall);
        }
    }

    private InvalidOperationException GetDuplicateRegistrationException(string route, IInboxHandler replacement)
    {
        try
        {
            _inbox.RegisterHandler(route, replacement);
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Duplicate exact route registration did not throw.");
    }

    private static string GetRouteLookupExceptionParameterName(Func<bool> lookup)
    {
        try
        {
            lookup();
        }
        catch (ArgumentException exception)
        {
            return exception.ParamName
                ?? throw new InvalidOperationException("Invalid route lookup exception did not identify its parameter.");
        }

        throw new InvalidOperationException("Invalid route lookup did not throw.");
    }
}
