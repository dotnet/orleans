---
date: 2026-01-17
researcher: OpenCode
git_commit: 96ffc33a489c46f4a09d15ee0b9c5ba68dfac248
branch: feature/durabletask/6
repository: orleans6
topic: "Consolidating Inbox/Outbox into Unified Durable Messaging System for Orleans.DurableTask Replatforming"
tags: [research, codebase, orleans-journaling, durable-messaging, inbox-outbox, durable-rpc, durable-task, system-architecture]
status: complete
last_updated: 2026-01-17
last_updated_by: OpenCode
---

# Research: Durable Messaging System Consolidation

## Research Question

We are in the midst of replatforming Orleans.DurableTask atop Orleans.Journaling. As a part of that, we have been creating a layer in Orleans.Journaling for durable messaging/RPC (using an inbox + outbox pattern). We aim to consolidate the inbox and outbox into a single, well-integrated durable messaging system. The durable messaging system stores messages in its inbox while they are being processed and it stores responses in its outbox. Some responses include a destination address (taken from the inbox message's ReplyTo metadata). Other responses will be retrieved when a caller polls for a response. The durable RPC system allows callers to long-poll for responses. Some of this functionality exists in Orleans.DurableTask already. Orleans.DurableTask is a system for Durable Execution, similar to Temporal or Azure Durable Functions. This effort is about improving the layering of the system.

## Summary

The codebase contains a comprehensive durable messaging system built on Orleans.Journaling with the following key components:

1. **Orleans.Journaling Messaging Layer** - Provides inbox/outbox patterns with exactly-once delivery semantics, deferred deserialization, and hierarchical correlation tracking
2. **Orleans.DurableTask System** - Three-layer architecture (Core.Abstractions → Runtime → Journaling) for durable task execution with observer-based completion notifications
3. **System.Distributed.DurableTasks** - Orleans-agnostic framework providing the async/await patterns for durable tasks
4. **Orleans.DurableJobs** - Background job scheduling system that powers the inbox/outbox message pumps

The current implementation already has substantial infrastructure for a unified durable messaging system. The key consolidation opportunities lie in:
- Unifying the response delivery patterns (ReplyTo-based routing vs. observer polling)
- Leveraging the existing CorrelationKey hierarchical tracking for request/response matching
- Integrating the DurableTask observer pattern with the Journaling inbox/outbox infrastructure

There is also an opportunity to simplify and generalize inbox dispatch:
- Remove redundant parameters from handler APIs when the same data is already exposed via handler context
- Add capability-based handler selection (e.g., `CanHandle`) so callers do not need to pre-register each handled route
- Prefer route prefixes (e.g., `rpc/`) for route-family matching rather than per-route registration

---

## Detailed Findings

### 1. Orleans.Journaling Messaging System

The messaging layer provides durable, exactly-once message delivery between grains.

#### Core Components

| Component | File | Purpose |
|-----------|------|---------|
| `DurableInbox` | `src/Orleans.Journaling/Messaging/DurableInbox.cs` | Stores pending messages with deduplication |
| `DurableOutbox` | `src/Orleans.Journaling/Messaging/DurableOutbox.cs` | Stores outgoing messages for delivery |
| `DurableEnvelope` | `src/Orleans.Journaling/Messaging/DurableEnvelope.cs` | Immutable message container with metadata |
| `DurableEnvelopeBuilder` | `src/Orleans.Journaling/Messaging/DurableEnvelopeBuilder.cs` | Fluent API for constructing envelopes |
| `DurableInboxExtension` | `src/Orleans.Journaling/Messaging/DurableInboxExtension.cs` | Grain extension for message delivery |

#### Inbox Architecture

**Storage Model** (`DurableInbox.cs:14-17`):
- `_inbox`: `IDurableDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope>` - Pending messages
- `_processed`: `IDurableDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset>` - Processed messages for deduplication
- `_handlers`: `Dictionary<string, IInboxHandler>` - Route key to handler mapping

**Message Lifecycle**:
1. **Delivery** (`DurableInboxExtension.cs:140-265`): Message arrives via `DeliverAsync()`
2. **Deduplication** (lines 148-187): Checks `_processed` and `_inbox` for existing message
3. **Capacity Check** (lines 190-207): Returns `Backpressured` if at capacity
4. **Route Validation** (lines 210-234): Ensures handler exists for route key
5. **Persistence** (lines 237-240): Atomically stores in `_inboxDict` via `WriteStateAsync()`
6. **Processing** (lines 412-582): Handler invoked, message removed, marked processed

**Proposed change: capability-based handler selection with prefix routing**

Motivation:
- `IInboxHandler.HandleAsync` currently receives the envelope, but the envelope is already available via `IInboxHandlerContext` (e.g., `context.Envelope`). Passing it separately duplicates data and expands the API surface.
- Per-route registration forces the system to “know” the full set of route keys ahead of time. For durable RPC and other evolving route families, prefix-based routing is more ergonomic (for example: everything under `rpc/`).

Proposal (conceptual):
1. Remove the `envelope` parameter from `IInboxHandler.HandleAsync(...)` and rely on `context.Envelope`.
2. Add `CanHandle(...)` which inspects the message (typically `context.Envelope.RouteKey` and metadata) and returns whether the handler can handle it.
3. Dispatch selects a handler by evaluating `CanHandle` over available handlers rather than requiring a pre-registered route key.

Prefix matching conventions:
- Reserve stable route “namespaces” such as:
    - `rpc/` for durable RPC request/response routes
    - `durabletask/` (or similar) for DurableTask transport messages
- Keep route keys hierarchical and human-readable.

Operational considerations:
- Ambiguity: if multiple handlers return `true`, define deterministic precedence (for example: registration order, or “longest prefix wins” if handlers advertise a prefix).
- Performance: if handler sets grow, consider caching `routeKey` → handler resolution per-activation.
- Safety: `CanHandle` should avoid full body deserialization by default; prefer route key and metadata checks, and use the existing TryGet/TryDeserialize patterns when body inspection is needed.

**Deferred Deserialization** (`DurableEnvelopeData.cs:27-165`):
- Messages stored as `ArcBuffer` slices with offset/length indices
- Body deserialized on-demand via `TryGetBody<T>()` - returns false on failure instead of throwing
- Context values independently deserializable via `TryGetContextValue<T>()`
- Prevents grain crashes from corrupted/missing message types during recovery

#### Outbox Architecture

**Storage Model** (`DurableOutbox.cs:41-62`):
- Inherits from `DurableDictionary<Guid, DurableEnvelope>` for durable storage
- `_pendingMessageIds`: `HashSet<Guid>` - Tracks messages not yet durably persisted
- `_pumpVersion`: Version counter for pump coordination

**Delivery Flow**:
1. **Send** (`DurableOutbox.cs:120-135`): Message added to dictionary, tracked as pending
2. **WriteCompleted** (lines 141-151): Clears pending set, schedules pump
3. **Pump Loop** (lines 374-443): Background task delivers messages
4. **Per-Message Delivery** (lines 216-317): Calls `targetGrain.DeliverAsync()`
5. **Result Handling**: Removes on success/duplicate, retries on backpressure

**Key Design Decision** (lines 132-134): Pump NOT triggered until after `OnWriteCompleted()` - ensures messages are durable before delivery.

#### Message Envelope Structure

**DurableEnvelope Fields** (`DurableEnvelope.cs:40-261`):
```csharp
public readonly struct DurableEnvelope
{
    public Guid MessageId { get; init; }           // Unique ID for deduplication
    public GrainId SenderId { get; init; }         // Origin grain
    public GrainId ReceiverId { get; init; }       // Target grain
    public string RouteKey { get; init; }          // Handler dispatch key
    public CorrelationKey? CorrelationKey { get; init; } // Hierarchical correlation
    public GrainId? ReplyTo { get; init; }         // Response destination
    public DurableEnvelopeData Data { get; init; } // Serialized payload
    public DateTimeOffset CreatedAt { get; init; } // Creation timestamp
}
```

### 2. Durable RPC and Long-Polling

#### DeliveryOptions Structure (`DeliveryOptions.cs:11-24`)
```csharp
[GenerateSerializer]
public struct DeliveryOptions
{
    [Id(0)] public TimeSpan PollTimeout { get; init; } = TimeSpan.Zero;
    [Id(1)] public GrainId? Observer { get; init; }
}
```

#### Long-Polling Implementation (`DurableInboxExtension.cs:270-304`)

**Pattern**:
1. Caller specifies `PollTimeout > TimeSpan.Zero` in `DeliveryOptions`
2. Extension creates `TaskCompletionSource<DeliveryResult>` and stores in `_pendingDeliveries`
3. `Task.WhenAny()` races between processing completion and timeout
4. Returns `DeliveryResult.Processed(response)` or `DeliveryResult.Pending()`

**Current Limitation**: Outbox pump uses `PollTimeout = TimeSpan.Zero` (`DurableOutbox.cs:225`), so no long-polling during outbox delivery. Responses come back as separate inbox messages.

#### Response Routing Patterns

**Pattern 1: ReplyTo-Based Routing** (Current primary pattern)
- Sender sets `ReplyTo` to their `GrainId`
- Handler checks `context.Envelope.ReplyTo` and sends response envelope
- Response arrives via sender's inbox

Proposed convention:
- Use two standard durable RPC routes:
    - `rpc/request` for requests
    - `rpc/reply` for replies (success or failure)
- Encode reply status (success/failure, retriable, error code) in message metadata and/or payload rather than using reserved `$*` route keys.

**Pattern 2: Long-Polling** (Partial implementation)
- Caller waits on `DeliverAsync()` with `PollTimeout`
- Response returned synchronously if processing completes within timeout
- Falls back to `Pending` status on timeout

**Pattern 3: Observer Callback** (`IDurableInboxObserver`)
- Grain implements `IDurableInboxObserver` interface
- Messages with matching `CorrelationKey` invoke `OnResponseAsync()`
- Provides callback-style notification without explicit ReplyTo

### 3. Correlation and Addressing

#### CorrelationKey (`CorrelationKey.cs`)

**Hierarchical Design**:
- Segments separated by `/` with `\` escape character
- Parent/child relationships: `transfer-123/debit`, `transfer-123/credit`
- Methods: `IsParentOf()`, `IsChildOf()`, `IsAncestorOf()`, `CreateChildKey()`

**Example**:
```csharp
var parentKey = CorrelationKey.Create("transfer-123");
var debitKey = parentKey.CreateChildKey("debit");   // "transfer-123/debit"
var creditKey = parentKey.CreateChildKey("credit"); // "transfer-123/credit"

Assert.True(parentKey.IsParentOf(debitKey));
Assert.True(parentKey.IsAncestorOf(debitKey));
```

**Usage in Handlers** (`DurableRpcIntegrationTests.cs:428-445`):
```csharp
if (context.Envelope.ReplyTo is { } replyTo)
{
    var response = context.CreateEnvelope()
        .To(replyTo, "rpc/reply")
        .WithBody(result)
        .WithCorrelationKey(context.Envelope.CorrelationKey) // Preserve correlation
        .Build();
    context.Send(response);
}
```

### 4. Orleans.DurableTask Architecture

#### Three-Layer Design

| Layer | Package | Purpose |
|-------|---------|---------|
| Abstractions | `Orleans.Core.Abstractions/DurableTasks/` | Contracts and interfaces |
| Runtime | `Orleans.Runtime/DurableTasks/` | In-memory execution |
| Journaling | `Orleans.Journaling/DurableTasks/` | Event-sourced persistence |

#### Key Interfaces

**IDurableTaskGrainRuntime** (`IDurableTaskGrainRuntime.cs:90-95`):
- `ScheduleChildAsync()` - Schedules child tasks with hierarchical IDs
- `GetScheduledTaskHandle()` - Retrieves task handles by ID

**IDurableTaskServer** (`IDurableTaskGrainRuntime.cs:22-37`):
- `ScheduleAsync()` - Idempotent task scheduling from remote grains
- `SubscribeOrPollAsync()` - Polls or subscribes for completion
- `CancelAsync()` - Request task cancellation

**IDurableTaskObserver** (`IDurableTaskGrainRuntime.cs:12-20`):
- `OnResponseAsync()` - Callback when task completes
- Marked `[AlwaysInterleave]` to avoid deadlocks

#### Observer Pattern for Completion

**Scheduling Flow** (`DurableTaskGrainRuntime.cs:104-161`):
1. Remote grain calls `ScheduleAsync()` with task and caller's `GrainId`
2. Runtime creates/retrieves task state
3. Caller added as observer via `AddObserver()`
4. Task executed via `Invoke()`
5. On completion, `NotifyClientsAndCleanupTask()` calls `OnResponseAsync()` on all observers

**This is the existing polling/callback pattern in DurableTask** - observers are notified when tasks complete, similar to what the unified messaging system needs for response delivery.

#### Journaling Storage (`DurableTaskGrainStorage.cs`)

Uses event sourcing with command pattern:
- Commands: `CreateTask`, `SetResult`, `AddObserver`, `ClearObservers`, `RemoveTask`, `RequestTaskCancellation`
- Each mutation applies to in-memory state and appends log entry
- Recovery replays log via `Apply()` method

### 5. System.Distributed.DurableTasks

**Purpose**: Orleans-agnostic layer for durable task execution with custom async/await patterns.

**Key Concept**: Deferred Execution
- Regular Task: Starts immediately when method called
- DurableTask: Execution deferred until awaited or scheduled
- State machine boxed via custom `DurableTaskMethodBuilder`

**Key Types**:
- `DurableTask` / `DurableTask<TResult>` - Task abstractions with deferred execution
- `DurableTaskAwaiter` - Custom awaiter for async/await integration
- `DurableTaskMethodBuilder` - Boxes state machine, defers execution
- `TaskId` / `HierarchicalKey` - Hierarchical task identification

**Relationship to Orleans**: Provides the async pattern foundation; Orleans.DurableTasks implements storage and grain integration.

### 6. Orleans.DurableJobs

**Purpose**: Distributed job scheduling for one-time future execution.

**Key Integration**: Powers Orleans.Journaling message pumps:
- `InboxProcessingPump` implements `IDurableJobHandler`
- `OutboxDeliveryPump` implements `IDurableJobHandler`
- Jobs scheduled immediately when messages added
- Auto-reschedule if messages remain after processing

**Architecture**:
- Time-based sharding (default 1-hour windows)
- Automatic shard rebalancing on cluster topology changes
- Exponential backoff retry with configurable policy
- Silo-level concurrency limits via semaphore

### 7. Message Processing Pumps

#### InboxProcessingPump (`InboxProcessingPump.cs`)

**Execution Flow**:
1. Grain adds message to inbox
2. Pump scheduled via `ILocalDurableJobManager.ScheduleJobAsync()`
3. Job invokes `ExecuteJobAsync()` which processes all pending messages
4. Each message routed to registered `IInboxHandler`
5. After handler completion, message removed and marked processed
6. State persisted atomically via `WriteStateAsync()`
7. Auto-reschedules if messages remain

**Error Handling**:
- Handler exceptions: Configurable removal vs. retry
- Outer exception catch: Always remove to prevent poison messages
- Concurrent processing guards via `ContainsOrProcessed` pattern

#### OutboxDeliveryPump (`OutboxDeliveryPump.cs`)

**Execution Flow**:
1. Handler sends message via `context.Send(envelope)`
2. Message stored in outbox (not yet durable)
3. `WriteStateAsync()` persists outbox, triggers `OnWriteCompleted()`
4. Pump scheduled for immediate execution
5. Messages delivered to target inboxes via `DeliverAsync()`
6. On `Accepted`: Remove from outbox
7. On `Backpressured`: Exponential backoff retry (1s → 60s max)
8. Auto-reschedules with calculated delay

---

## Code References

### Orleans.Journaling Messaging
- `src/Orleans.Journaling/Messaging/DurableInbox.cs:14-148` - Inbox storage and handler registration
- `src/Orleans.Journaling/Messaging/DurableOutbox.cs:41-496` - Outbox with background delivery pump
- `src/Orleans.Journaling/Messaging/DurableEnvelope.cs:40-261` - Message envelope structure
- `src/Orleans.Journaling/Messaging/DurableInboxExtension.cs:140-582` - Delivery and processing
- `src/Orleans.Journaling/Messaging/CorrelationKey.cs:8-470` - Hierarchical correlation

### Orleans.DurableTask
- `src/Orleans.Core.Abstractions/DurableTasks/IDurableTaskGrainRuntime.cs:12-95` - Runtime interfaces
- `src/Orleans.Runtime/DurableTasks/DurableTaskGrainRuntime.cs:17-582` - Runtime implementation
- `src/Orleans.Journaling/DurableTasks/DurableTaskGrainStorage.cs:13-436` - Event-sourced storage

### System.Distributed.DurableTasks
- `src/System.Distributed.DurableTasks/DurableTask.cs:6-839` - Core abstractions
- `src/System.Distributed.DurableTasks/DurableTaskMethodBuilder.cs:9-118` - Custom async builder
- `src/System.Distributed.DurableTasks/HierarchicalKey.cs:8-599` - Key implementation

### Message Pumps
- `src/Orleans.Journaling/Messaging/InboxProcessingPump.cs:36-346` - Inbox processing
- `src/Orleans.Journaling/Messaging/OutboxDeliveryPump.cs:37-326` - Outbox delivery

### Orleans.DurableJobs
- `src/Orleans.DurableJobs/LocalDurableJobManager.cs:18-351` - Job scheduling
- `src/Orleans.DurableJobs/ShardExecutor.cs:17-141` - Job execution

---

## Architecture Documentation

### Current Layering

```
┌─────────────────────────────────────────────────────────────────┐
│                    Application Layer                             │
│  (User grains implementing IDurableJobHandler, IInboxHandler)   │
└─────────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────┴───────────────────────────────────┐
│                Orleans.DurableTask Layer                         │
│  ┌──────────────────┐  ┌──────────────────┐  ┌────────────────┐ │
│  │ DurableTask-     │  │ DurableTask-     │  │ DurableTask-   │ │
│  │ GrainRuntime     │  │ GrainStorage     │  │ Request        │ │
│  │ (Execution)      │  │ (Journaled)      │  │ (Remote Calls) │ │
│  └──────────────────┘  └──────────────────┘  └────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────┴───────────────────────────────────┐
│              Orleans.Journaling Messaging Layer                  │
│  ┌──────────────────┐  ┌──────────────────┐  ┌────────────────┐ │
│  │ DurableInbox     │  │ DurableOutbox    │  │ DurableEnvelope│ │
│  │ + Extension      │  │ + DeliveryPump   │  │ + Builder      │ │
│  └──────────────────┘  └──────────────────┘  └────────────────┘ │
│  ┌──────────────────┐  ┌──────────────────┐  ┌────────────────┐ │
│  │ InboxProcessing  │  │ OutboxDelivery   │  │ CorrelationKey │ │
│  │ Pump             │  │ Pump             │  │ + Addressing   │ │
│  └──────────────────┘  └──────────────────┘  └────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────┴───────────────────────────────────┐
│             Orleans.Journaling Foundation Layer                  │
│  ┌──────────────────┐  ┌──────────────────┐  ┌────────────────┐ │
│  │ StateMachine-    │  │ Durable-         │  │ LogExtent +    │ │
│  │ Manager          │  │ Dictionary/List  │  │ Storage        │ │
│  └──────────────────┘  └──────────────────┘  └────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────┴───────────────────────────────────┐
│                    Orleans.DurableJobs                           │
│  (Background job scheduling for message pumps)                   │
└─────────────────────────────────────────────────────────────────┘
```

### Message Flow: Request/Response with ReplyTo

```
┌─────────────┐                    ┌─────────────┐
│   Sender    │                    │   Handler   │
│   Grain     │                    │   Grain     │
└─────────────┘                    └─────────────┘
      │                                   │
      │ 1. Create envelope               │
      │    .WithReplyTo(this.GrainId)    │
      │    .WithCorrelationKey("...")    │
      │                                   │
      │ 2. context.Send(envelope)         │
      │    → Added to outbox              │
      │                                   │
      │ 3. WriteStateAsync()              │
      │    → Outbox persisted             │
      │    → Pump triggered               │
      │                                   │
      │──────── 4. DeliverAsync() ───────▶│
      │                                   │
      │                                   │ 5. Handler processes
      │                                   │
      │                                   │ 6. if (ReplyTo) {
      │                                   │      CreateEnvelope()
      │                                   │      .To(replyTo, "response")
      │                                   │      .WithBody(result)
      │                                   │      .Send()
      │                                   │    }
      │                                   │
      │                                   │ 7. WriteStateAsync()
      │                                   │    → Response in outbox
      │                                   │
      │◀─────── 8. DeliverAsync() ────────│
      │                                   │
      │ 9. Response handler invoked       │
      │                                   │
```

### DurableTask Observer Pattern (Existing)

```
┌─────────────┐                    ┌─────────────┐
│   Caller    │                    │   Target    │
│   Grain     │                    │   Grain     │
└─────────────┘                    └─────────────┘
      │                                   │
      │ 1. await grainRef.DurableMethod() │
      │                                   │
      │──── 2. ScheduleAsync(request) ───▶│
      │        (caller subscribes as      │
      │         observer)                 │
      │                                   │ 3. Task executes
      │                                   │
      │◀─ 4. OnResponseAsync(response) ───│
      │      (observer callback)          │
      │                                   │
      │ 5. Response returned to caller    │
      │                                   │
```

### Consolidation Opportunity

The two patterns above serve similar purposes:
- **Journaling ReplyTo**: Fire-and-forget with eventual response delivery
- **DurableTask Observer**: Immediate callback on completion

A unified system could:
1. Use `CorrelationKey` for request/response matching (already exists)
2. Support both push (observer callback) and pull (inbox polling) response delivery
3. Leverage `IDurableInboxObserver` for the callback pattern
4. Store pending responses in outbox with optional ReplyTo or Observer address

---

## Key Patterns and Design Decisions

### 1. Exactly-Once Delivery Semantics
- **Composite Key**: `(SenderId, MessageId)` for deduplication
- **Processed Tracking**: Separate dictionary tracks processed messages
- **Atomic Persistence**: Messages persisted atomically with grain state
- **Deduplication Window**: Configurable retention (default 7 days)

### 2. Deferred Deserialization (MigrationContext Pattern)
- Body and context values stored as `ArcBuffer` slices
- `TryGet*` methods return false on failure (no exceptions)
- Prevents grain crashes from type version mismatches

### 3. Backpressure and Flow Control
- Inbox capacity limits (configurable, default 1000)
- `DeliveryStatus.Backpressured` signals sender to retry
- Exponential backoff in outbox pump (1s → 60s max)
- Per-target backoff tracking

### 4. Thread Safety and Grain Context
- **Critical**: `ConfigureAwait(true)` used throughout to maintain grain synchronization context
- Version-based pump coordination prevents race conditions
- Lock + volatile read for safe task state management

### 5. Hierarchical Correlation
- `CorrelationKey` supports parent/child relationships
- Enables distributed tracing across sub-operations
- `IsAncestorOf()`, `IsChildOf()` for relationship queries

### 6. Event Sourcing for State
- Mutations logged immediately via `IStateMachineLogWriter`
- Recovery replays log to rebuild in-memory state
- Periodic snapshots for compaction

---

## Open Questions (with Answers)

1. **Long-Polling Gap**: Outbox pump currently uses `PollTimeout = TimeSpan.Zero`. Should the unified system support synchronous-style RPC with actual long-polling?

    **Answer**: Not required for the outbox pump because reply addresses exist (grains are addressable and can use `ReplyTo`). Long polling can be considered later if needed.

2. **Observer vs. ReplyTo Unification**: How should the DurableTask observer pattern be unified with the Journaling ReplyTo pattern? Both solve similar problems with different mechanisms.

    **Answer**: Yes—unify them, and prefer the `ReplyTo` model as the primary mechanism.

3. **Response Storage Location**: Should responses be stored in the responder's outbox (current) or the requester's inbox directly (via observer callback)?

    **Answer**: Store the response in the responder’s outbox until `IDurableInboxExtension.DeliverAsync(...)` returns successfully to the requester, indicating the response has been durably delivered and can be removed from the outbox.

4. **CorrelationKey for DurableTask**: The Journaling layer has `CorrelationKey`; DurableTask uses `TaskId` (which is `HierarchicalKey`). Should these be unified or bridged?

    **Answer**: Unify them by using `HierarchicalKey` directly (remove `TaskId` and `CorrelationKey` in favor of `HierarchicalKey`).

5. **Polling vs. Callback Trade-offs**: When should callers poll for responses vs. receive callbacks? The unified system should support both patterns elegantly.

    **Answer**: Grains should always use `ReplyTo`. External callers do not have stable addresses, so they use polling (including long-polling where appropriate).

6. **Cleanup Policies**: How should completed request/response pairs be garbage collected? DurableTask has `CleanupPolicy` with 1-day default; Journaling has deduplication window.

    **Answer**:
    - Outbox messages are eligible for cleanup when `IDurableInboxExtension.DeliverAsync(...)` returns successfully (durable delivery), or when the deduplication window lapses.
    - Inbox messages are eligible for cleanup when the corresponding outbox message is added.

7. **Error Propagation**: How should handler exceptions propagate to callers in the unified system? DurableTask wraps in `ExceptionDurableTaskResponse`; Journaling just logs and optionally removes.

    **Answer**: Permanent failures (e.g., cancellation or business-logic failures) should be delivered to the caller as failure responses. Failures travel through the outbox the same way success responses do.

8. **Handler Dispatch Model**: Should the system move from explicit route key registration (`Dictionary<string, IInboxHandler>`) to capability-based handler selection (e.g., `CanHandle`) with prefix routing such as `rpc/`? If so, what precedence rules apply when multiple handlers match, and should the runtime cache route-to-handler resolution?

    **Answer**: Yes. Enumerate handlers in order of registration to resolve precedence.

---

## Related Research

This is the initial research document for this consolidation effort. Future research may include:
- Specific design proposals for the unified messaging system
- Performance analysis of current patterns
- Comparison with external systems (Temporal, Azure Durable Functions)
