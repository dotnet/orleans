# Technical Design Document: Unified Durable Messaging System

| Field | Value |
|-------|-------|
| **Author** | OpenCode |
| **Date** | 2026-01-17 |
| **Status** | Draft |
| **Branch** | feature/durabletask/6 |
| **Research Doc** | [2026-01-17-durable-messaging-system-consolidation.md](./2026-01-17-durable-messaging-system-consolidation.md) |

---

## Executive Summary

This TDD describes the consolidation of Orleans.Journaling's inbox/outbox messaging system with Orleans.DurableTask's observer pattern into a unified durable messaging system. The goal is to improve layering, reduce code duplication, and provide a single, well-integrated durable messaging foundation for Orleans.DurableTask replatforming.

### Key Changes

1. **Unify `CorrelationKey` and `TaskId`** into a single `HierarchicalKey` type
2. **Simplify `IInboxHandler` API** by removing redundant envelope parameter and adding `CanHandle()` for capability-based dispatch
3. **Implement prefix-based routing** (e.g., `rpc/`, `durabletask/`) instead of per-route registration
4. **Standardize response delivery** using `ReplyTo` as the primary mechanism for grains
5. **Implement response acknowledgment** with outbox cleanup on inbox receipt

---

## Motivation

### Current State

The codebase has two overlapping patterns for durable request/response:

1. **Orleans.Journaling Inbox/Outbox** (`src/Orleans.Journaling/Messaging/`)
   - Uses `ReplyTo` field in `DurableEnvelope` for response routing
   - Uses `CorrelationKey` for hierarchical correlation tracking
   - Handlers registered per route key via `Dictionary<string, IInboxHandler>`

2. **Orleans.DurableTask Observer Pattern** (`src/Orleans.Runtime/DurableTasks/`)
   - Uses `IDurableTaskObserver.OnResponseAsync()` for callbacks
   - Uses `TaskId` (backed by `HierarchicalKey`) for task identification
   - Observers registered and notified on task completion

Both patterns solve similar problems with different mechanisms, leading to:
- Code duplication between messaging and task layers
- Two different hierarchical key types (`CorrelationKey` vs `HierarchicalKey`)
- Inconsistent response delivery patterns
- Handler APIs with redundant parameters

### Goals

1. **Single hierarchical key type** for correlation across all durable messaging
2. **Capability-based handler dispatch** with prefix routing
3. **Unified response delivery** using `ReplyTo` for addressable grains
4. **Cleaner handler API** without redundant parameters
5. **Response acknowledgment** for reliable cleanup

---

## Detailed Design

### 1. HierarchicalKey Unification

**Decision**: Replace both `CorrelationKey` and `TaskId` with a unified `HierarchicalKey` type.

#### Current State

| Type | Location | Purpose |
|------|----------|---------|
| `CorrelationKey` | `src/Orleans.Journaling/Messaging/CorrelationKey.cs` | Message correlation |
| `HierarchicalKey` | `src/System.Distributed.DurableTasks/HierarchicalKey.cs` | Task identification |

Both types are nearly identical:
- Same segment separator (`/`) and escape character (`\`)
- Same parent/child/ancestor relationship methods
- Same parsing and formatting logic

#### Changes

1. **Promote `HierarchicalKey` to `Orleans.Core.Abstractions`**
   - Move from `System.Distributed.DurableTasks` to `Orleans.Core.Abstractions`
   - Change from `internal sealed class` to `public sealed class`
   - Add `[GenerateSerializer]` and `[Immutable]` attributes

2. **Update `DurableEnvelope`** to use `HierarchicalKey`:
   ```csharp
   // Before
   [Id(4)]
   public CorrelationKey? CorrelationKey { get; init; }

   // After
   [Id(4)]
   public HierarchicalKey? CorrelationKey { get; init; }
   ```

3. **Deprecate `CorrelationKey`** with forwarding to `HierarchicalKey`:
   ```csharp
   [Obsolete("Use HierarchicalKey instead. This type will be removed in a future version.")]
   public sealed class CorrelationKey
   {
       private readonly HierarchicalKey _inner;

       public static implicit operator HierarchicalKey(CorrelationKey key) => key._inner;
       public static implicit operator CorrelationKey(HierarchicalKey key) => new(key);
       // ... delegate all methods to _inner
   }
   ```

4. **Update `TaskId`** to be a type alias:
   ```csharp
   // In System.Distributed.DurableTasks
   public readonly struct TaskId
   {
       public HierarchicalKey Key { get; }
       public static implicit operator HierarchicalKey(TaskId id) => id.Key;
       public static implicit operator TaskId(HierarchicalKey key) => new(key);
   }
   ```

#### Files to Modify

| File | Change |
|------|--------|
| `src/Orleans.Core.Abstractions/HierarchicalKey.cs` | New file (moved from System.Distributed.DurableTasks) |
| `src/Orleans.Journaling/Messaging/CorrelationKey.cs` | Deprecate, delegate to HierarchicalKey |
| `src/Orleans.Journaling/Messaging/DurableEnvelope.cs` | Change CorrelationKey to HierarchicalKey |
| `src/Orleans.Journaling/Messaging/DurableEnvelopeBuilder.cs` | Update correlation key methods |
| `src/System.Distributed.DurableTasks/HierarchicalKey.cs` | Remove (or keep as type-forward) |
| `src/System.Distributed.DurableTasks/TaskId.cs` | Update to wrap HierarchicalKey |

---

### 2. IInboxHandler API Changes

**Decision**: Remove redundant `envelope` parameter from `HandleAsync` and add `CanHandle()` for capability-based dispatch.

#### Current API

```csharp
// src/Orleans.Journaling/Messaging/IInboxHandler.cs:47-62
public interface IInboxHandler
{
    ValueTask HandleAsync(DurableEnvelope envelope, IInboxHandlerContext context, CancellationToken cancellationToken);
}

public interface IInboxHandler<TMessage> : IInboxHandler
{
    ValueTask HandleAsync(TMessage message, IInboxHandlerContext context, CancellationToken cancellationToken);
}
```

**Issues**:
1. `envelope` parameter is redundant - already available via `context.Envelope`
2. Route-based registration requires knowing all route keys upfront
3. No capability-based selection for route families (e.g., all `rpc/*` messages)

#### New API

```csharp
namespace Orleans.Journaling.Messaging;

/// <summary>
/// Handler for durable inbox messages with capability-based dispatch.
/// </summary>
public interface IInboxHandler
{
    /// <summary>
    /// Determines whether this handler can process the message.
    /// </summary>
    /// <param name="context">Handler context with envelope metadata.</param>
    /// <returns>True if this handler can process the message; otherwise, false.</returns>
    /// <remarks>
    /// <para>
    /// This method should perform fast, metadata-only checks. Avoid deserializing
    /// the message body. Use <c>context.Envelope.RouteKey</c> for route matching
    /// and <c>context.Envelope.CorrelationKey</c> for correlation checks.
    /// </para>
    /// <para>
    /// Common patterns:
    /// <list type="bullet">
    /// <item><description>Exact match: <c>context.Envelope.RouteKey == "payment/process"</c></description></item>
    /// <item><description>Prefix match: <c>context.Envelope.RouteKey.StartsWith("rpc/")</c></description></item>
    /// <item><description>Correlation match: <c>context.Envelope.CorrelationKey?.IsChildOf(myKey) == true</c></description></item>
    /// </list>
    /// </para>
    /// </remarks>
    bool CanHandle(IInboxHandlerContext context);

    /// <summary>
    /// Handles a message from the inbox.
    /// </summary>
    /// <param name="context">Handler context for accessing envelope and sending responses.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
    ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Typed handler with automatic deserialization.
/// </summary>
public interface IInboxHandler<TMessage> : IInboxHandler
{
    /// <summary>
    /// Handles a typed message.
    /// </summary>
    ValueTask HandleAsync(TMessage message, IInboxHandlerContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Default implementation: deserialize and delegate.
    /// </summary>
    ValueTask IInboxHandler.HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        if (context.Envelope.Data.TryGetBody<TMessage>(out var message))
        {
            return HandleAsync(message, context, cancellationToken);
        }
        throw new InvalidOperationException($"Failed to deserialize message body as {typeof(TMessage).Name}");
    }
}
```

#### Handler Registration Changes

```csharp
// Current: explicit route registration
public interface IDurableInbox
{
    void RegisterHandler(string routeKey, IInboxHandler handler);
    bool HasHandler(string routeKey);
    bool TryGetHandler(string routeKey, out IInboxHandler handler);
}

// New: ordered handler list with capability-based selection
public interface IDurableInbox
{
    /// <summary>
    /// Registers a handler. Handlers are evaluated in registration order.
    /// </summary>
    void RegisterHandler(IInboxHandler handler);

    /// <summary>
    /// Finds the first handler that can process the message.
    /// </summary>
    /// <param name="context">The handler context.</param>
    /// <param name="handler">The matched handler, if found.</param>
    /// <returns>True if a handler was found; otherwise, false.</returns>
    bool TryFindHandler(IInboxHandlerContext context, out IInboxHandler handler);

    // Backward compatibility: route-based registration creates a RouteKeyHandler wrapper
    [Obsolete("Use RegisterHandler(IInboxHandler) with CanHandle() instead.")]
    void RegisterHandler(string routeKey, IInboxHandler handler);
}
```

#### Helper Base Classes

```csharp
/// <summary>
/// Base class for handlers that match exact route keys.
/// </summary>
public abstract class RouteKeyHandler : IInboxHandler
{
    private readonly string _routeKey;

    protected RouteKeyHandler(string routeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        _routeKey = routeKey;
    }

    public bool CanHandle(IInboxHandlerContext context)
        => context.Envelope.RouteKey == _routeKey;

    public abstract ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Base class for handlers that match route key prefixes.
/// </summary>
public abstract class RoutePrefixHandler : IInboxHandler
{
    private readonly string _prefix;

    protected RoutePrefixHandler(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        _prefix = prefix.EndsWith('/') ? prefix : prefix + '/';
    }

    /// <summary>
    /// Gets the route suffix after the prefix.
    /// </summary>
    protected string GetRouteSuffix(IInboxHandlerContext context)
        => context.Envelope.RouteKey[_prefix.Length..];

    public bool CanHandle(IInboxHandlerContext context)
        => context.Envelope.RouteKey.StartsWith(_prefix, StringComparison.Ordinal);

    public abstract ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Base class for handlers that match correlation key relationships.
/// </summary>
public abstract class CorrelationHandler : IInboxHandler
{
    private readonly HierarchicalKey _correlationKey;

    protected CorrelationHandler(HierarchicalKey correlationKey)
    {
        ArgumentNullException.ThrowIfNull(correlationKey);
        _correlationKey = correlationKey;
    }

    public bool CanHandle(IInboxHandlerContext context)
        => context.Envelope.CorrelationKey?.IsChildOf(_correlationKey) == true
           || context.Envelope.CorrelationKey?.Equals(_correlationKey) == true;

    public abstract ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken);
}
```

#### Files to Modify

| File | Change |
|------|--------|
| `src/Orleans.Journaling/Messaging/IInboxHandler.cs` | Update API: remove envelope param, add CanHandle |
| `src/Orleans.Journaling/Messaging/IDurableInbox.cs` | Update registration API |
| `src/Orleans.Journaling/Messaging/DurableInbox.cs` | Change from dictionary to ordered list |
| `src/Orleans.Journaling/Messaging/RouteKeyHandler.cs` | New file |
| `src/Orleans.Journaling/Messaging/RoutePrefixHandler.cs` | New file |
| `src/Orleans.Journaling/Messaging/CorrelationHandler.cs` | New file |
| `src/Orleans.Journaling/Messaging/InboxProcessingPump.cs` | Update handler dispatch logic |
| `src/Orleans.Journaling/Messaging/DurableInboxExtension.cs` | Update route validation |

---

### 3. Response Routing and Delivery Semantics

**Decision**: Use `ReplyTo` as the primary response routing mechanism. Responses are stored in the responder's outbox until `IDurableInboxExtension.DeliverAsync(...)` returns successfully to the requester, at which point the responder knows the response has been durably delivered and it can be removed from the outbox.

#### Response Flow

```
┌──────────────────┐                         ┌──────────────────┐
│   Requester      │                         │   Responder      │
│   Grain          │                         │   Grain          │
└──────────────────┘                         └──────────────────┘
        │                                            │
        │ 1. Create request with ReplyTo             │
        │    envelope.ReplyTo = this.GrainId         │
        │    envelope.CorrelationKey = "req-123"     │
        │                                            │
        │ 2. Add to outbox                           │
        ├────────────────────────────────────────────▶
        │                                            │
        │ 3. WriteStateAsync() persists outbox       │
        │                                            │
        │ 4. Outbox pump delivers to inbox           │
        │                                            │
        │                                            │ 5. Handler processes
        │                                            │
        │                                            │ 6. Handler creates response
        │                                            │    .To(envelope.ReplyTo, "rpc/reply")
        │                                            │    .WithCorrelationKey(envelope.CorrelationKey)
        │                                            │
        │                                            │ 7. Add response to outbox
        │                                            │
        │ 8. Response delivered to inbox             │
        ◀────────────────────────────────────────────┤
        │                                            │
        │ 9. Response handler invoked                │
        │                                            │
        │                                            │ 10. Responder removes response from outbox
        │                                            │     after DeliverAsync returns successfully
```

#### Delivery Semantics and Outbox Cleanup

The delivery API already provides the durable acknowledgment we need:

- When `IDurableInboxExtension.DeliverAsync(...)` returns successfully, the caller knows the message has been durably delivered (persisted by the receiver’s inbox).
- Therefore, the sender can remove the message from its outbox immediately on success, without a separate `$ack` message.

This applies equally to “normal” messages and responses routed via `ReplyTo`.

#### Files to Modify

No separate acknowledgment message or handler is required.

---

### 4. Error Handling and Failure Responses

**Decision**: Permanent failures are delivered as failure responses through the outbox.

We do not require separate `$error` or `$response` routes. Instead, replies (success or failure) use the same route key (e.g., `rpc/reply`) and encode status either:
- in message metadata (envelope metadata/context), or
- in the payload itself.

#### Failure Response Structure

```csharp
/// <summary>
/// Envelope for error responses.
/// </summary>
[GenerateSerializer]
public sealed class DurableErrorResponse
{
    /// <summary>
    /// Error code for categorization.
    /// </summary>
    [Id(0)]
    public required string ErrorCode { get; init; }

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    [Id(1)]
    public required string Message { get; init; }

    /// <summary>
    /// Optional exception details (for debugging, not for production).
    /// </summary>
    [Id(2)]
    public string? ExceptionDetails { get; init; }

    /// <summary>
    /// Indicates whether the error is retriable.
    /// </summary>
    [Id(3)]
    public bool IsRetriable { get; init; }
}
```

#### Error Codes

| Code | Description |
|------|-------------|
| `HANDLER_NOT_FOUND` | No handler registered for route key |
| `DESERIALIZATION_FAILED` | Failed to deserialize message body |
| `HANDLER_EXCEPTION` | Handler threw an unhandled exception |
| `CANCELLED` | Operation was cancelled |
| `TIMEOUT` | Operation timed out |

#### Handler Context Extension

```csharp
public interface IInboxHandlerContext
{
    // Existing members...

    /// <summary>
    /// Sends a failure reply to the ReplyTo address (if set).
    /// </summary>
    void SendError(string errorCode, string message, bool isRetriable = false);

    /// <summary>
    /// Sends an error response from an exception.
    /// </summary>
    void SendError(Exception exception, bool isRetriable = false);
}
```

#### Implementation

```csharp
// In InboxHandlerContext
public void SendError(string errorCode, string message, bool isRetriable = false)
{
    if (Envelope.ReplyTo is not { } replyTo)
    {
        return; // No reply address - log and discard
    }

    var response = CreateEnvelope()
        .To(replyTo, "rpc/reply")
        .WithBody(new DurableErrorResponse
        {
            ErrorCode = errorCode,
            Message = message,
            IsRetriable = isRetriable
        })
        .WithCorrelationKey(Envelope.CorrelationKey)
        .Build();

    Send(response);
}
```

---

### 5. Route Key Conventions

The system can avoid special `$*` routes by standardizing on two primary durable RPC routes and encoding reply status in metadata/payload.

User route keys should use descriptive prefixes:

| Prefix | Purpose |
|--------|---------|
| `rpc/request` | Durable RPC requests |
| `rpc/reply` | Durable RPC replies (success or failure) |
| `durabletask/` | DurableTask transport messages |
| `job/` | DurableJob scheduling |

---

### 6. External Caller Support (Polling)

**Decision**: Grains use `ReplyTo`; external callers without stable addresses use polling.

External callers (e.g., HTTP endpoints, non-grain callers) cannot receive inbox messages because they lack a stable `GrainId`. For these cases, the existing long-polling pattern is retained:

```csharp
// External caller pattern
var result = await targetGrain.AsReference<IDurableInboxExtension>()
    .DeliverAsync(envelope, new DeliveryOptions { PollTimeout = TimeSpan.FromSeconds(30) });

switch (result.Status)
{
    case DeliveryStatus.Processed:
        var response = result.Response; // Synchronous response
        break;
    case DeliveryStatus.Pending:
        // Poll for completion via separate endpoint
        break;
}
```

This is an existing pattern (`DurableInboxExtension.cs:270-304`) and requires no changes.

---

## Migration Strategy

### Phase 1: Add New APIs (Non-Breaking)

1. Add `HierarchicalKey` to `Orleans.Core.Abstractions`
2. Add `CanHandle()` to `IInboxHandler` with default implementation
3. Add `RegisterHandler(IInboxHandler)` overload to `IDurableInbox`
4. Add helper base classes (`RouteKeyHandler`, `RoutePrefixHandler`, `CorrelationHandler`)

### Phase 2: Update Internal Usage

1. Update Orleans.DurableTask to use new `HierarchicalKey`
2. Update Orleans.Journaling handlers to use `CanHandle()` pattern
3. Add error response handling

### Phase 3: Deprecate Old APIs

1. Mark `CorrelationKey` as `[Obsolete]`
2. Mark `RegisterHandler(string, IInboxHandler)` as `[Obsolete]`
3. Mark `IInboxHandler.HandleAsync(DurableEnvelope, ...)` as `[Obsolete]`

### Phase 4: Remove Deprecated APIs (Future Major Version)

1. Remove `CorrelationKey` type
2. Remove old registration methods
3. Remove old handler signatures

---

## Testing Strategy

### Unit Tests

| Test Area | File |
|-----------|------|
| HierarchicalKey parsing and hierarchy | `test/NonSilo.Tests/HierarchicalKeyTests.cs` |
| CanHandle routing logic | `test/NonSilo.Tests/InboxHandlerRoutingTests.cs` |
| Error response serialization | `test/NonSilo.Tests/DurableErrorResponseTests.cs` |

### Integration Tests

| Test Area | File |
|-----------|------|
| End-to-end request/response | `test/DefaultCluster.Tests/DurableRpcIntegrationTests.cs` |
| Handler registration ordering | `test/DefaultCluster.Tests/HandlerPrecedenceTests.cs` |
| Migration compatibility | `test/DefaultCluster.Tests/CorrelationKeyMigrationTests.cs` |

---

## Open Questions

1. **Handler Caching**: Should we cache `routeKey -> handler` resolutions for performance after first lookup?
   - **Recommendation**: Yes, add a `ConcurrentDictionary<string, IInboxHandler?>` cache that is invalidated when handlers are registered.

2. **Backward Compatibility**: How long should deprecated APIs be maintained?
   - **Recommendation**: Minimum 2 minor versions before removal in next major version.

---

## Appendix: Code References

### Current Implementation

| Component | File | Lines |
|-----------|------|-------|
| IInboxHandler | `src/Orleans.Journaling/Messaging/IInboxHandler.cs` | 47-143 |
| IInboxHandlerContext | `src/Orleans.Journaling/Messaging/IInboxHandlerContext.cs` | 58-215 |
| DurableEnvelope | `src/Orleans.Journaling/Messaging/DurableEnvelope.cs` | 40-261 |
| DurableInbox | `src/Orleans.Journaling/Messaging/DurableInbox.cs` | 12-149 |
| CorrelationKey | `src/Orleans.Journaling/Messaging/CorrelationKey.cs` | 28-765 |
| HierarchicalKey | `src/System.Distributed.DurableTasks/HierarchicalKey.cs` | 8-599 |
| InboxProcessingPump | `src/Orleans.Journaling/Messaging/InboxProcessingPump.cs` | 36-346 |
| OutboxDeliveryPump | `src/Orleans.Journaling/Messaging/OutboxDeliveryPump.cs` | 37-326 |
| DurableInboxExtension | `src/Orleans.Journaling/Messaging/DurableInboxExtension.cs` | 140-582 |

### DurableTask Integration Points

| Component | File | Purpose |
|-----------|------|---------|
| IDurableTaskObserver | `src/Orleans.Core.Abstractions/DurableTasks/IDurableTaskGrainRuntime.cs:12-20` | Observer callback interface |
| DurableTaskGrainRuntime | `src/Orleans.Runtime/DurableTasks/DurableTaskGrainRuntime.cs` | Task execution runtime |
| DurableTaskGrainStorage | `src/Orleans.Journaling/DurableTasks/DurableTaskGrainStorage.cs` | Event-sourced storage |
