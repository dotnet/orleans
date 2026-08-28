using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.DurableMessaging;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;

namespace Orleans.DurableMessaging.Tests.Support;

public interface IDurableMessagingTestGrain : IGrainWithGuidKey
{
    Task<Guid> SendAsync(GrainId target, string route, DurableTestMessage message);
    Task<Guid> SendDuplicateAsync(GrainId target, string route, DurableTestMessage message);
    Task<Guid> SendAndDeactivateAsync(GrainId target, string route, DurableTestMessage message);
    Task<Guid> StageWithoutCommitAsync(GrainId target, string route, DurableTestMessage message);
    Task DeleteThenWriteStateAsync();
    [AlwaysInterleave] Task RetryWriteStateAsync();
    [AlwaysInterleave] Task RevertStateAsync();
    Task SetInboxJobIdAsync(string jobId);
    [AlwaysInterleave] Task DeactivateOnNextRecoveryAsync();
    Task<DuplicateRouteRegistrationResult> RegisterDuplicateExactRouteHandlersAsync(string route);
    Task<RouteLookupValidationResult> ValidateRouteLookupAsync(string? route);
    Task<bool> RemoveInboxDeadLetterAsync(GrainId senderId, Guid messageId);
    Task<bool> RemoveOutboxDeadLetterAsync(Guid messageId);
    [AlwaysInterleave] Task<DurableEndpointSnapshot> GetSnapshotAsync();
    Task RequestDeactivationAsync();
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
    [property: Id(4)] bool ThrowAfterStaging = false,
    [property: Id(5)] bool CommitDuringHandling = false,
    [property: Id(6)] bool DeleteDuringHandling = false);

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
    [property: Id(14)] int NullNullableValueMessageCalls);

[GenerateSerializer, Immutable]
public sealed record DurableDeadLetterSnapshot(
    [property: Id(0)] Guid MessageId,
    [property: Id(1)] string Route,
    [property: Id(2)] string Reason,
    [property: Id(3)] int AttemptCount,
    [property: Id(4)] DateTimeOffset DeadLetteredAt);

[GrainType("durable-messaging-public-test")]
public sealed class DurableMessagingTestGrain : DurableGrain, IDurableMessagingTestGrain, IJournaledStateObserver
{
    private readonly IDurableInbox _inbox;
    private readonly IDurableOutbox _outbox;
    private readonly IDurableMessagingDiagnostics _diagnostics;
    private readonly IDurableDictionary<Guid, DurableEffect> _effects;
    private readonly IDurableDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> _processedMessages;
    private readonly SerializerSessionPool _sessions;
    private readonly IDurableValue<string> _inboxJobId;
    private readonly IDurableValue<string> _outboxJobId;
    private readonly ILocalSiloDetails _siloDetails;
    private readonly HandlerProbe _handlerProbe;
    private readonly SnapshotProbe _snapshotProbe;
    private readonly Guid _activationId = Guid.NewGuid();
    private int _activeHandlers;
    private int _maxConcurrentHandlers;
    private int _firstExactRouteHandlerCalls;
    private int _replacementExactRouteHandlerCalls;
    private int _nullReferenceMessageCalls;
    private int _nullNullableValueMessageCalls;
    private bool _deactivateOnNextRecovery;

    public DurableMessagingTestGrain(
        IDurableInbox inbox,
        IDurableOutbox outbox,
        IDurableMessagingDiagnostics diagnostics,
        [FromKeyedServices("test-effects")] IDurableDictionary<Guid, DurableEffect> effects,
        [FromKeyedServices("inbox")] IDurableValue<string> applicationInboxState,
        [FromKeyedServices("__orleans.durable-messaging.inbox-processed")] IDurableDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> processedMessages,
        [FromKeyedServices("__orleans.durable-messaging.inbox-job-id")] IDurableValue<string> inboxJobId,
        [FromKeyedServices("__orleans.durable-messaging.outbox-job-id")] IDurableValue<string> outboxJobId,
        SerializerSessionPool sessions,
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
        _outboxJobId = outboxJobId;
        _sessions = sessions;
        _siloDetails = siloDetails;
        _handlerProbe = handlerProbe;
        _snapshotProbe = snapshotProbe;
        StateManager.RegisterObserver(this);
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _inbox.RegisterHandler("nullable/reference", new NullReferenceMessageHandler(this));
        _inbox.RegisterHandler("nullable/value", new NullNullableValueMessageHandler(this));
        _inbox.RegisterHandler(new TypedMessageHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task<Guid> SendAsync(GrainId target, string route, DurableTestMessage message)
    {
        var envelope = CreateEnvelope(target, route, message);
        _outbox.Send(envelope);
        await WriteStateAsync();
        return envelope.MessageId;
    }

    public async Task<Guid> SendDuplicateAsync(GrainId target, string route, DurableTestMessage message)
    {
        var envelope = CreateEnvelope(target, route, message);
        _outbox.Send(envelope);
        _outbox.Send(envelope);
        await WriteStateAsync();
        return envelope.MessageId;
    }

    public async Task<Guid> SendAndDeactivateAsync(GrainId target, string route, DurableTestMessage message)
    {
        var messageId = await SendAsync(target, route, message);
        DeactivateOnIdle();
        return messageId;
    }

    public Task<Guid> StageWithoutCommitAsync(GrainId target, string route, DurableTestMessage message)
    {
        var envelope = CreateEnvelope(target, route, message);
        _outbox.Send(envelope);
        return Task.FromResult(envelope.MessageId);
    }

    public async Task DeleteThenWriteStateAsync()
    {
        await StateManager.DeleteStateAsync(CancellationToken.None);
        await WriteStateAsync();
    }

    public async Task RetryWriteStateAsync() => await WriteStateAsync();

    public async Task RevertStateAsync() => await StateManager.RevertPendingChangesAsync(CancellationToken.None);

    public async Task SetInboxJobIdAsync(string jobId)
    {
        _inboxJobId.Value = jobId;
        await WriteStateAsync();
    }

    public Task DeactivateOnNextRecoveryAsync()
    {
        _deactivateOnNextRecovery = true;
        return Task.CompletedTask;
    }

    public Task<DuplicateRouteRegistrationResult> RegisterDuplicateExactRouteHandlersAsync(string route)
    {
        var first = new CountingHandler(() => _firstExactRouteHandlerCalls++);
        var replacement = new CountingHandler(() => _replacementExactRouteHandlerCalls++);
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

    public async Task<bool> RemoveOutboxDeadLetterAsync(Guid messageId)
    {
        if (!_diagnostics.RemoveOutboxDeadLetter(messageId))
        {
            return false;
        }

        await WriteStateAsync();
        return true;
    }

    public Task<DurableEndpointSnapshot> GetSnapshotAsync() => Task.FromResult(CreateSnapshot());

    public Task RequestDeactivationAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public void OnWriteStarted()
    {
    }

    public void OnWriteCompleted() => PublishSnapshot();
    public void OnRecoveryCompleted()
    {
        PublishSnapshot();
        if (_deactivateOnNextRecovery)
        {
            _deactivateOnNextRecovery = false;
            DeactivateOnIdle();
        }
    }

    private DurableEnvelope CreateEnvelope(GrainId target, string route, DurableTestMessage message) =>
        new DurableEnvelopeBuilder(_sessions, this.GetGrainId())
            .To(target, route)
            .WithBody(message)
            .Build();

    private async ValueTask HandleAsync(
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

            _effects.TryGetValue(message.LogicalId, out var prior);
            _effects[message.LogicalId] = new DurableEffect(
                message.LogicalId,
                (prior?.Count ?? 0) + 1,
                message.Sequence,
                message.Value);

            if (message.CommitDuringHandling)
            {
                await WriteStateAsync(cancellationToken);
            }

            if (message.DeleteDuringHandling)
            {
                await StateManager.DeleteStateAsync(cancellationToken);
            }

            if (message.ForwardTo is { } target)
            {
                var outgoing = context.CreateEnvelope()
                    .To(target, "messages/forwarded")
                    .WithBody(message with { ForwardTo = null, ThrowAfterStaging = false })
                    .Build();
                context.Send(outgoing);
            }

            if (message.ThrowAfterStaging)
            {
                throw new InvalidOperationException($"Injected handler failure for {message.LogicalId}.");
            }
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
            _firstExactRouteHandlerCalls,
            _replacementExactRouteHandlerCalls,
            _outboxJobId.Value,
            _nullReferenceMessageCalls,
            _nullNullableValueMessageCalls);

    private static DurableDeadLetterSnapshot ToSnapshot(DurableDeadLetter deadLetter) =>
        new(
            deadLetter.Message.MessageId,
            deadLetter.Message.RouteKey,
            deadLetter.Reason,
            deadLetter.AttemptCount,
            deadLetter.DeadLetteredAt);

    private sealed class TypedMessageHandler(DurableMessagingTestGrain owner) : IInboxHandler<DurableTestMessage>
    {
        bool IInboxHandler.CanHandle(IInboxHandlerContext context) =>
            context.Envelope.RouteKey.StartsWith("messages/", StringComparison.Ordinal)
            || context.Envelope.RouteKey == "typed";

        public ValueTask HandleAsync(
            DurableTestMessage? message,
            IInboxHandlerContext context,
            CancellationToken cancellationToken) =>
            owner.HandleAsync(
                message ?? throw new InvalidOperationException("A durable test message is required."),
                context,
                cancellationToken);
    }

    private sealed class NullReferenceMessageHandler(DurableMessagingTestGrain owner) : IInboxHandler<string?>
    {
        public ValueTask HandleAsync(
            string? message,
            IInboxHandlerContext context,
            CancellationToken cancellationToken)
        {
            if (message is not null)
            {
                throw new InvalidOperationException("Expected a null reference message.");
            }

            owner._nullReferenceMessageCalls++;
            return default;
        }
    }

    private sealed class NullNullableValueMessageHandler(DurableMessagingTestGrain owner) : IInboxHandler<int?>
    {
        public ValueTask HandleAsync(
            int? message,
            IInboxHandlerContext context,
            CancellationToken cancellationToken)
        {
            if (message is not null)
            {
                throw new InvalidOperationException("Expected a null nullable value message.");
            }

            owner._nullNullableValueMessageCalls++;
            return default;
        }
    }

    private sealed class CountingHandler(Action onCall) : IInboxHandler
    {
        public bool CanHandle(IInboxHandlerContext context) => true;

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            onCall();
            return default;
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
