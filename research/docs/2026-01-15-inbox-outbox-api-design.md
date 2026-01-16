---
date: 2026-01-15
researcher: Claude
git_commit: d9e43924fa5a069ce9e3db8e84c76bf6f43bf810
branch: feature/durabletask/5
repository: orleans6
topic: "Inbox/Outbox API Design for Orleans Grains"
tags: [research, codebase, inbox-outbox, api-design, idempotency, durable-jobs, journaling]
status: complete
last_updated: 2026-01-15
last_updated_by: Claude
depends_on:
  - 2026-01-15-durable-tasks-journaling-architecture.md
---

# Research: Inbox/Outbox API Design for Orleans Grains

## Research Question

Design developer-facing APIs for inbox/outbox abstractions in Orleans that:
1. Enable atomically consuming inbox messages, updating grain state, and enqueueing outbox messages
2. Guarantee exactly-once/idempotent message processing
3. Feel natural for Orleans grain developers
4. Layer cleanly atop `IStateMachineManager` and existing durable primitives
5. Support refactoring `Orleans.DurableJobs` to use these abstractions

## Summary

This document surveys inbox/outbox patterns from five major .NET frameworks (MassTransit, NServiceBus, Wolverine, Rebus, Azure Durable Functions) and proposes Orleans-specific API designs that leverage the unique strengths of the virtual actor model and `Orleans.Journaling`.

**Key Recommendations:**

1. **Use extension-based opt-in** via `IDurableInboxExtension<T>` and `IDurableOutboxExtension<T>` grain extensions
2. **Leverage keyed DI** pattern already established in Orleans.Journaling (`[FromKeyedServices("inbox")]`)
3. **Idempotency via `(GrainId, MessageId)` composite keys** stored in `IDurableDictionary`
4. **Integrate with `DurableGrain` base class** for seamless `WriteStateAsync()` atomicity
5. **Provide attribute-based opt-in** (`[DurableInbox]`, `[DurableOutbox]`) for simplicity

---

## External Framework Survey

### Comparison Table: Framework Features

| Feature | MassTransit | NServiceBus | Wolverine | Rebus | Azure Durable Functions |
|---------|-------------|-------------|-----------|-------|------------------------|
| **Inbox Support** | Yes | Yes | Yes | Saga only | Implicit |
| **Outbox Support** | Yes | Yes | Yes | TransactionContext | Implicit |
| **Message ID Tracking** | MessageId + ConsumerId | MessageId | Envelope ID | MessageId (Saga) | Entity ID + Operation |
| **Deduplication Window** | Configurable | Configurable | Configurable | N/A (state-based) | N/A (state-based) |
| **Storage Tables** | 3 | 1+ | 3 | Saga table only | Azure Storage internal |
| **Atomicity Mechanism** | DB Transaction | DB Transaction | DB Transaction | DB Transaction | Entity operation atomicity |
| **Configuration Style** | Fluent API + Policies | Fluent API | Fluent API + Attributes | Fluent API | Attributes + Function bindings |

### MassTransit

**Opt-in API:**
```csharp
x.AddEntityFrameworkOutbox<RegistrationDbContext>(o =>
{
    o.UsePostgres();
    o.UseBusOutbox();
    o.DuplicateDetectionWindow = TimeSpan.FromDays(7);
});
```

**Storage Schema:**
- `InboxState`: `(MessageId, ConsumerId)` composite key for deduplication
- `OutboxState`: Tracks delivery status
- `OutboxMessage`: Pending outgoing messages

**Atomicity:** Single DB transaction wraps handler execution + outbox writes.

### NServiceBus

**Opt-in API:**
```csharp
endpointConfiguration.EnableOutbox();
```

**Transparent to handlers** - outbox is automatic once enabled. Handlers look identical whether outbox is enabled or not.

**Idempotency:** `IOutboxStorage.Get(messageId)` before processing; stored operations replayed on retry.

### Wolverine

**Opt-in API:**
```csharp
// Attribute-based
[Transactional]
public static async Task Handle(DebitAccount command, ...)

// Or policy-based
opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
opts.Policies.UseDurableInboxOnAllListeners();
```

**Three tables:** `incoming_envelopes`, `outgoing_envelopes`, `dead_letters`

**Atomicity:** `RawDatabaseEnvelopeTransaction` wraps message persistence in application's `DbContext` transaction.

### Rebus

**Idempotent Saga pattern only:**
```csharp
public class MySaga : IdempotentSaga<MySagaData>, IHandleMessages<MyMessage>
{
    public async Task Handle(MyMessage message)
    {
        // Outgoing messages stored in IdempotencyData
        await Bus.Send(new AnotherMessage());
    }
}
```

**Limitation:** Regular handlers must implement idempotency manually.

### Azure Durable Functions

**Built-in guarantees through architecture:**
- Entity operations are serialized (one at a time per entity)
- State persisted after each operation
- No explicit inbox/outbox tables needed
- Similar to Orleans grains - leverages single-threaded execution

---

## Orleans-Specific Considerations

### Advantages of Virtual Actor Model

1. **Single-threaded grain execution** - No concurrent message processing per grain
2. **Grain identity** - Natural `(GrainId, MessageId)` composite key for deduplication
3. **Turn-based execution** - State reads/writes are naturally serialized
4. **`IStateMachineManager` atomicity** - Multiple state machines persist atomically

### Existing Patterns to Leverage

1. **Keyed DI for state machines** (`src/Orleans.Journaling/DurableQueue.cs:35`):
   ```csharp
   public DurableQueue<T>([ServiceKey] string key, IStateMachineManager manager, ...)
   ```

2. **Grain extension model** (`src/Orleans.DurableJobs/IDurableJobReceiverExtension.cs`):
   ```csharp
   internal interface IDurableJobReceiverExtension : IGrainExtension
   {
       Task DeliverDurableJobAsync(IDurableJobContext context, CancellationToken cancellationToken);
   }
   ```

3. **DurableGrain base class** (`src/Orleans.Journaling/DurableGrain.cs:34`):
   ```csharp
   protected ValueTask WriteStateAsync(CancellationToken cancellationToken = default) 
       => StateMachineManager.WriteStateAsync(cancellationToken);
   ```

4. **Experimental DurableChannel** (`src/Orleans.Journaling/Messaging/DurableChannel.cs`):
   ```csharp
   private readonly IDurableQueue<OutboxMessage> _outbox;
   private readonly IDurableQueue<InboxMessage> _inbox;
   private readonly IDurableDictionary<(GrainId, IdempotencyKey), MessageProcessingState> _processingState;
   ```

---

## Proposed API Design

### Option 1: Extension-Based (Recommended)

#### Core Interfaces

```csharp
namespace Orleans.Journaling.Messaging;

/// <summary>
/// Marker interface for inbox messages. All inbox message types must be serializable.
/// </summary>
public interface IInboxMessage
{
    /// <summary>
    /// Unique identifier for this message instance, used for deduplication.
    /// </summary>
    Guid MessageId { get; }
}

/// <summary>
/// Provides durable inbox functionality for grains.
/// Messages are persisted and deduplicated before processing.
/// </summary>
public interface IDurableInbox<TMessage> where TMessage : IInboxMessage
{
    /// <summary>
    /// Gets the number of unprocessed messages in the inbox.
    /// </summary>
    int Count { get; }
    
    /// <summary>
    /// Attempts to dequeue the next message for processing.
    /// Returns false if inbox is empty.
    /// </summary>
    bool TryDequeue([MaybeNullWhen(false)] out TMessage message);
    
    /// <summary>
    /// Peeks at the next message without removing it.
    /// </summary>
    bool TryPeek([MaybeNullWhen(false)] out TMessage message);
    
    /// <summary>
    /// Checks if a message has already been processed (idempotency check).
    /// </summary>
    bool IsProcessed(Guid messageId);
    
    /// <summary>
    /// Marks a message as processed. Call after successful handling.
    /// </summary>
    void MarkProcessed(Guid messageId);
}

/// <summary>
/// Provides durable outbox functionality for grains.
/// Messages are persisted atomically with grain state updates.
/// </summary>
public interface IDurableOutbox<TMessage>
{
    /// <summary>
    /// Enqueues a message for delivery to the specified grain.
    /// The message is persisted with the next WriteStateAsync() call.
    /// </summary>
    void Send(GrainId target, TMessage message);
    
    /// <summary>
    /// Enqueues a message for delivery via the specified grain reference.
    /// </summary>
    void Send<TGrain>(TGrain grain, TMessage message) where TGrain : IAddressable;
}

/// <summary>
/// Extension interface for receiving durable inbox messages.
/// Grains with this extension can receive messages durably.
/// </summary>
public interface IDurableInboxExtension<TMessage> : IGrainExtension where TMessage : IInboxMessage
{
    /// <summary>
    /// Delivers a message to the grain's inbox. Called by the outbox delivery system.
    /// </summary>
    ValueTask DeliverAsync(GrainId sender, TMessage message, CancellationToken cancellationToken);
}
```

#### Usage Example

```csharp
namespace MyApp.Grains;

// Define inbox message type
[GenerateSerializer, Immutable]
public record TransferRequest(
    [property: Id(0)] Guid MessageId,
    [property: Id(1)] string SourceAccount,
    [property: Id(2)] string DestinationAccount,
    [property: Id(3)] decimal Amount
) : IInboxMessage;

[GenerateSerializer, Immutable]
public record TransferCompleted(
    [property: Id(0)] Guid MessageId,
    [property: Id(1)] string TransactionId,
    [property: Id(2)] bool Success
) : IInboxMessage;

// Grain implementation
public class BankAccountGrain : DurableGrain, IBankAccountGrain, IDurableJobHandler
{
    private readonly IDurableValue<decimal> _balance;
    private readonly IDurableInbox<TransferRequest> _inbox;
    private readonly IDurableOutbox<TransferCompleted> _outbox;
    
    public BankAccountGrain(
        [FromKeyedServices("balance")] IDurableValue<decimal> balance,
        [FromKeyedServices("inbox")] IDurableInbox<TransferRequest> inbox,
        [FromKeyedServices("outbox")] IDurableOutbox<TransferCompleted> outbox)
    {
        _balance = balance;
        _inbox = inbox;
        _outbox = outbox;
    }
    
    public async Task ExecuteJobAsync(IDurableJobContext context, CancellationToken cancellationToken)
    {
        // Process inbox messages (triggered by DurableJob or timer)
        while (_inbox.TryDequeue(out var request))
        {
            // Idempotency check
            if (_inbox.IsProcessed(request.MessageId))
            {
                continue;
            }
            
            // Business logic
            var success = TryDebit(request.Amount);
            
            // Send response via outbox
            _outbox.Send(
                GrainId.Parse(request.SourceAccount),
                new TransferCompleted(Guid.NewGuid(), context.Job.Id, success));
            
            // Mark as processed
            _inbox.MarkProcessed(request.MessageId);
        }
        
        // Atomic persist: balance + inbox state + outbox messages
        await WriteStateAsync(cancellationToken);
    }
    
    private bool TryDebit(decimal amount)
    {
        if (_balance.Value >= amount)
        {
            _balance.Value -= amount;
            return true;
        }
        return false;
    }
}
```

### Option 2: Attribute-Based

```csharp
namespace Orleans.Journaling.Messaging;

/// <summary>
/// Marks a grain as having a durable inbox.
/// Enables automatic inbox delivery infrastructure.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class DurableInboxAttribute : Attribute
{
    public Type MessageType { get; }
    public string Key { get; set; } = "inbox";
    
    public DurableInboxAttribute(Type messageType)
    {
        MessageType = messageType;
    }
}

/// <summary>
/// Marks a grain as having a durable outbox.
/// Enables automatic outbox delivery infrastructure.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class DurableOutboxAttribute : Attribute
{
    public Type MessageType { get; }
    public string Key { get; set; } = "outbox";
    
    public DurableOutboxAttribute(Type messageType)
    {
        MessageType = messageType;
    }
}

// Usage
[DurableInbox(typeof(TransferRequest))]
[DurableOutbox(typeof(TransferCompleted))]
public class BankAccountGrain : DurableGrain, IBankAccountGrain
{
    // Inbox/outbox injected automatically based on attributes
}
```

### Option 3: Handler Interface (NServiceBus-style)

```csharp
namespace Orleans.Journaling.Messaging;

/// <summary>
/// Interface for grains that handle inbox messages of a specific type.
/// Provides transparent idempotency and outbox integration.
/// </summary>
public interface IDurableMessageHandler<TMessage> where TMessage : IInboxMessage
{
    /// <summary>
    /// Handles a message. The framework guarantees exactly-once processing.
    /// Outbox messages sent during handling are persisted atomically.
    /// </summary>
    ValueTask HandleAsync(TMessage message, IMessageContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Context available during message handling.
/// </summary>
public interface IMessageContext
{
    /// <summary>
    /// The ID of the grain that sent this message.
    /// </summary>
    GrainId SenderId { get; }
    
    /// <summary>
    /// Sends a message via the outbox (persisted atomically).
    /// </summary>
    void Send<T>(GrainId target, T message);
    
    /// <summary>
    /// Sends a message via the outbox to a grain reference.
    /// </summary>
    void Send<TGrain, TMessage>(TGrain grain, TMessage message) where TGrain : IAddressable;
}

// Usage
public class BankAccountGrain : DurableGrain, IBankAccountGrain, IDurableMessageHandler<TransferRequest>
{
    public async ValueTask HandleAsync(
        TransferRequest message, 
        IMessageContext context, 
        CancellationToken cancellationToken)
    {
        // Idempotency handled automatically by framework
        var success = TryDebit(message.Amount);
        
        // Outbox handled automatically
        context.Send(message.SourceAccountGrainId, new TransferCompleted(...));
        
        // WriteStateAsync called automatically after handler completes
    }
}
```

---

## Idempotency Strategy

### Recommended: Composite Key Tracking

```csharp
/// <summary>
/// Tracks message processing state for idempotency.
/// </summary>
[GenerateSerializer]
internal struct MessageProcessingState
{
    [Id(0)] public DateTimeOffset ReceivedAt { get; set; }
    [Id(1)] public DateTimeOffset? ProcessedAt { get; set; }
    [Id(2)] public ProcessingStatus Status { get; set; }
    [Id(3)] public int Attempts { get; set; }
}

internal enum ProcessingStatus
{
    Received,
    Processing,
    Processed,
    Failed
}
```

**Storage:** `IDurableDictionary<(GrainId SenderId, Guid MessageId), MessageProcessingState>`

**Deduplication Flow:**
1. On message delivery: Check if `(senderId, messageId)` exists in dictionary
2. If exists with `Status == Processed`: Skip (duplicate)
3. If exists with `Status == Processing`: Log warning, continue (redelivery during processing)
4. If not exists: Add entry with `Status = Received`
5. Process message
6. Update entry: `Status = Processed`, `ProcessedAt = now`
7. `WriteStateAsync()` - persists inbox state + grain state + outbox atomically

### Deduplication Window Cleanup

```csharp
public interface IDurableInboxOptions
{
    /// <summary>
    /// How long to keep processed message IDs for deduplication.
    /// Default: 7 days
    /// </summary>
    TimeSpan DeduplicationWindow { get; set; }
    
    /// <summary>
    /// How often to clean up old processing state entries.
    /// Default: 1 hour
    /// </summary>
    TimeSpan CleanupInterval { get; set; }
}
```

**Cleanup mechanism:** Grain timer or reminder that iterates `_processingState` and removes entries older than `DeduplicationWindow`.

---

## Outbox Delivery Strategy

### Background Delivery Service

```csharp
/// <summary>
/// Delivers outbox messages from grains to their destinations.
/// Runs as a per-silo system target.
/// </summary>
internal class OutboxDeliveryService : SystemTarget, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly IGrainFactory _grainFactory;
    private readonly IStateMachineStorageProvider _storage;
    
    public async Task DeliverPendingMessagesAsync(CancellationToken cancellationToken)
    {
        // Query grains with pending outbox messages
        // For each grain:
        //   1. Load outbox queue
        //   2. For each message: call target grain's inbox extension
        //   3. On success: dequeue from outbox
        //   4. WriteStateAsync() to persist outbox changes
    }
}
```

### Grain-Local Delivery (Simpler)

Alternatively, each grain can deliver its own outbox messages after `WriteStateAsync()`:

```csharp
internal class DurableOutbox<TMessage> : IDurableOutbox<TMessage>, IDurableStateMachine
{
    private readonly IDurableQueue<OutboxEntry<TMessage>> _queue;
    private readonly IGrainFactory _grainFactory;
    
    public async ValueTask DeliverPendingAsync(CancellationToken cancellationToken)
    {
        while (_queue.TryPeek(out var entry))
        {
            try
            {
                var extension = _grainFactory.GetGrain<IDurableInboxExtension<TMessage>>(entry.Target);
                await extension.DeliverAsync(entry.Sender, entry.Message, cancellationToken);
                _queue.TryDequeue(out _);
            }
            catch
            {
                // Retry later - message stays in outbox
                break;
            }
        }
    }
}
```

---

## DurableJobs Integration

### Current Architecture (`src/Orleans.DurableJobs/`)

```
LocalDurableJobManager
    └── ScheduleJobAsync() → JobShard.TryScheduleJobAsync()
                                └── InMemoryJobQueue.TryEnqueue()
                                └── PersistAddJobAsync() [NO-OP in InMemoryJobShard]

ShardExecutor
    └── RunShardAsync() → shard.ConsumeDurableJobsAsync()
                            └── IDurableJobReceiverExtension.DeliverDurableJobAsync()
```

**Problem:** `InMemoryJobShard.PersistAddJobAsync()` returns `Task.CompletedTask` - no durability.

### Proposed Refactoring

```csharp
/// <summary>
/// A durable job shard that persists state via Orleans.Journaling.
/// </summary>
public class DurableJobShard : JobShard, IDurableStateMachine
{
    private readonly IDurableQueue<DurableJob> _jobQueue;
    private readonly IDurableDictionary<string, JobProcessingState> _processingState;
    private readonly IStateMachineManager _stateMachineManager;
    
    protected override async Task PersistAddJobAsync(
        string jobId, string jobName, DateTimeOffset dueTime, 
        GrainId target, IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        // Jobs are already in-memory from base class
        // Just persist the state machines
        await _stateMachineManager.WriteStateAsync(cancellationToken);
    }
    
    protected override async Task PersistRemoveJobAsync(string jobId, CancellationToken cancellationToken)
    {
        await _stateMachineManager.WriteStateAsync(cancellationToken);
    }
    
    protected override async Task PersistRetryJobAsync(
        string jobId, DateTimeOffset newDueTime, CancellationToken cancellationToken)
    {
        await _stateMachineManager.WriteStateAsync(cancellationToken);
    }
}
```

### DurableJobs as Outbox Consumer

Alternatively, jobs can be modeled as outbox messages:

```csharp
/// <summary>
/// A durable job is conceptually an outbox message with a scheduled delivery time.
/// </summary>
[GenerateSerializer, Immutable]
public record DurableJobMessage(
    [property: Id(0)] Guid MessageId,
    [property: Id(1)] string JobId,
    [property: Id(2)] string JobName,
    [property: Id(3)] DateTimeOffset DueTime,
    [property: Id(4)] IReadOnlyDictionary<string, string>? Metadata
) : IInboxMessage;

/// <summary>
/// Grain extension for scheduling durable jobs.
/// Jobs are stored in the grain's outbox and delivered when due.
/// </summary>
public interface IDurableJobOutbox
{
    /// <summary>
    /// Schedules a job for delivery to the specified grain at the due time.
    /// </summary>
    void Schedule(GrainId target, string jobName, DateTimeOffset dueTime, IReadOnlyDictionary<string, string>? metadata = null);
}
```

This allows atomic scheduling:
```csharp
public async Task ProcessOrderAsync(Order order)
{
    // Update grain state
    _orders.Add(order.Id, order);
    
    // Schedule reminder job - stored in outbox
    _jobOutbox.Schedule(
        this.GetGrainId(),
        "check-payment-status",
        DateTimeOffset.UtcNow.AddHours(24),
        new Dictionary<string, string> { ["orderId"] = order.Id });
    
    // Both persisted atomically
    await WriteStateAsync();
}
```

---

## Implementation Roadmap

### Phase 1: Core Inbox/Outbox Primitives

1. **Enable `DurableChannel.cs`** - Remove `#if false` and complete implementation
2. **Add `IDurableInbox<T>` and `IDurableOutbox<T>` interfaces**
3. **Implement `DurableInbox<T>`** using `DurableQueue<T>` + `DurableDictionary` for processing state
4. **Implement `DurableOutbox<T>`** using `DurableQueue<OutboxEntry<T>>`
5. **Add keyed DI registration** for inbox/outbox state machines

### Phase 2: Grain Integration

1. **Create `IDurableInboxExtension<T>` grain extension**
2. **Add extension activation on grain startup** (similar to `DurableJobReceiverExtension`)
3. **Implement inbox delivery path** - sender grain's outbox → receiver grain's inbox extension
4. **Add delivery retry logic** with exponential backoff

### Phase 3: DurableJobs Refactoring

1. **Create `DurableJobShard` implementation** of `JobShard` using journaling
2. **Model jobs as scheduled outbox messages**
3. **Integrate with existing `ShardExecutor`** for delivery
4. **Add migration path** from `InMemoryJobShard`

### Phase 4: Developer Experience

1. **Add attribute-based opt-in** (`[DurableInbox]`, `[DurableOutbox]`)
2. **Create `IDurableMessageHandler<T>` interface** for transparent handling
3. **Add source generators** for boilerplate reduction
4. **Documentation and samples**

---

## Open Questions

1. **Outbox delivery timing:** Should delivery happen:
   - Immediately after `WriteStateAsync()` (lower latency, more grain load)?
   - Via background service (higher latency, less grain load)?
   - Hybrid (immediate attempt, background retry)?

2. **Multi-message handlers:** Should a grain support multiple inbox message types?
   - Single `IDurableMessageHandler<T>` per grain?
   - Multiple handlers via interface composition?

3. **Error handling:** What happens when inbox processing fails?
   - Dead letter queue?
   - Retry with backoff?
   - Alert/monitoring integration?

4. **Cross-silo optimization:** Should outbox messages to grains on the same silo bypass persistence?
   - Performance benefit vs. complexity tradeoff

5. **Transaction boundaries:** Should inbox/outbox support opt-in "ambient transactions" spanning multiple grains?
   - Similar to NServiceBus transaction scope support
   - Would require distributed transaction coordination

---

## Code References

### Orleans.Journaling
- `src/Orleans.Journaling/IStateMachineManager.cs:8-44` - Core atomicity abstraction
- `src/Orleans.Journaling/DurableQueue.cs:13-23` - Queue interface
- `src/Orleans.Journaling/DurableDictionary.cs` - Dictionary for processing state
- `src/Orleans.Journaling/DurableGrain.cs:34` - `WriteStateAsync()` integration
- `src/Orleans.Journaling/Messaging/DurableChannel.cs:87-146` - Experimental inbox/outbox

### Orleans.DurableJobs
- `src/Orleans.DurableJobs/IDurableJobHandler.cs:93-113` - Job handler interface
- `src/Orleans.DurableJobs/IDurableJobReceiverExtension.cs:12-21` - Extension pattern
- `src/Orleans.DurableJobs/InMemoryJobShard.cs:19-32` - No-op persistence hooks
- `src/Orleans.DurableJobs/LocalDurableJobManager.cs:55-103` - Job scheduling

### Playground Examples
- `playground/WorkflowsApp/WorkflowsApp.Service/Samples/Bank/Bank.cs` - DurableGrain usage

---

## Conclusion

The recommended approach combines:

1. **Extension-based architecture** (`IDurableInboxExtension<T>`) for clean separation
2. **Keyed DI injection** following established Orleans.Journaling patterns
3. **`IStateMachineManager.WriteStateAsync()` for atomicity** - inbox + state + outbox in single persist
4. **Composite key idempotency** `(GrainId, MessageId)` stored in `DurableDictionary`
5. **Integration with `DurableGrain` base class** for seamless developer experience

This design:
- Leverages Orleans' single-threaded grain execution (no complex concurrency)
- Reuses existing journaling primitives (`DurableQueue`, `DurableDictionary`)
- Provides clean migration path for DurableJobs
- Matches patterns from proven frameworks (MassTransit, Wolverine, NServiceBus)
