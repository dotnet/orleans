---
date: 2026-01-16
researcher: Claude
git_commit: d9e43924fa5a069ce9e3db8e84c76bf6f43bf810
branch: feature/durabletask/5
repository: orleans6
topic: "Durable Inbox/Outbox for Durable RPC (Request/Response)"
tags: [research, codebase, journaling, durable-tasks, inbox-outbox, grain-extensions, backpressure]
status: complete
last_updated: 2026-01-16
last_updated_by: Claude
depends_on:
  - 2026-01-15-inbox-outbox-api-design.md
  - 2026-01-15-durable-tasks-journaling-architecture.md
---

# Research: Durable Inbox/Outbox for Durable RPC (Request/Response)

## Research Question

Consider how a durable inbox and outbox can be paired to create durable RPC (request/response), which is the essence of `DurableTask<T>`.

Constraints and design goals to research/document:
- Inbox and outbox should support **many message types**, not a single message type, so interfaces likely should not be generic (unless the *envelope* is pluggable).
- Consider how **backpressure** should be expressed in inbox/outbox interfaces.
- Avoid **generic grain extension interfaces and methods** whenever possible.
- How does an extension ensure messages of a given type can be delivered to the grain (or to another grain extension, e.g. durable tasks grain extension)?

## Summary

The Orleans codebase already contains patterns for:

1. **Durable request/response** in `Orleans.DurableTasks` using:
   - a non-generic extension interface (`IDurableTaskGrainExtension`) as the stable delivery surface;
   - a polymorphic request type (`IDurableTaskRequest`) which represents an invocation and can be persisted;
   - correlation via `TaskId` (hierarchical) and `DurableTaskRequestContext`.
   
   See `src/Orleans.Runtime/DurableTasks/DurableTaskGrainRuntime.cs:104` and `src/Orleans.Core.Abstractions/DurableTasks/DurableTaskRequest.cs:154`.

2. **Non-generic grain extension multiplexing** over multiple “kinds” of deliveries using routing keys:
   - streaming consumer extension routes by `subscriptionId` (`IStreamConsumerExtension`)
   - transaction manager/resource extensions route by `resourceId` (`ITransactionManagerExtension`, `ITransactionalResourceExtension`)
   - broadcast channel consumer routes by `streamId` + runtime type-checking (`IBroadcastChannelConsumerExtension`)

3. **A proto-inbox/outbox envelope type** in the disabled experimental `DurableChannel.cs`, where `InboxMessage` and `OutboxMessage` carry an `object MessageBody` plus correlation/idempotency information.

Together, these show an Orleans-native strategy for a polymorphic durable inbox/outbox which supports request/response:
- Use **one non-generic inbox extension** with a single `DeliverAsync(Envelope)` method.
- Use **an envelope** which carries message identity, optional reply-to, and an encoded “route” which directs handling to either:
  - a grain method invocation representation (e.g., `IInvokable`/`IDurableTaskRequest`), or
  - another extension (e.g., durable tasks runtime extension).
- Use **routing keys** (like `resourceId`/`subscriptionId`) rather than generic extension interfaces to locate the right handler.

## Detailed Findings

### 1) Durable RPC semantics in Orleans.DurableTasks

#### Durable task request is a polymorphic request object

`IDurableTaskRequest` exists as a non-generic “request envelope” abstraction for durable tasks.

- `src/Orleans.Core.Abstractions/DurableTasks/DurableTaskRequest.cs:19` defines `IDurableTaskRequest`.
- `DurableTaskRequest` and `DurableTaskRequest<TResult>` implement `IDurableTaskRequest` and `ISchedulableTask`.
- Requests capture `DurableTaskRequestContext` which includes the remote `TargetId` and optionally a `CallerId`.

See `src/Orleans.Core.Abstractions/DurableTasks/DurableTaskRequest.cs:143` where a generated proxy calls `InitializeRequest(GrainReference)` to capture `TargetId`.

#### Scheduling is done via a non-generic grain extension interface

Requests are scheduled by calling a grain extension on the target grain:

- `DurableTaskRequest.ScheduleAsync` uses `GrainFactory.GetGrain<IDurableTaskGrainExtension>(Context.TargetId)` and invokes `ScheduleAsync(taskId, this)`.
  - `src/Orleans.Core.Abstractions/DurableTasks/DurableTaskRequest.cs:159-161`
  - `src/Orleans.Core.Abstractions/DurableTasks/DurableTaskRequest.cs:316-323`

This is a direct example of a “durable RPC envelope” (the request object) being delivered to a non-generic extension interface.

#### Runtime persists request, executes, persists response, then notifies

On the target grain activation:

- `DurableTaskGrainRuntime` implements both `IDurableTaskGrainRuntime` and `IDurableTaskGrainExtension`.
  - `src/Orleans.Runtime/DurableTasks/DurableTaskGrainRuntime.cs:17-20`

When a request arrives:

- `IDurableTaskServer.ScheduleAsync(TaskId, IDurableTaskRequest, CancellationToken)`
  - checks for existing task handle (idempotent scheduling)
  - stores request state in `IDurableTaskGrainStorage`
  - `await _storage.WriteAsync(...)` before invoking the task
  - then invokes `request.CreateTask()` locally and captures response
  - persists response and notifies observers

See `src/Orleans.Runtime/DurableTasks/DurableTaskGrainRuntime.cs:104-160`.

This sequence embodies the core durable RPC invariants:
- request is durable before execution
- response is durable before notifying clients

#### Correlation via TaskId + observers (reply-to)

The response is correlated to the request using the `TaskId`:

- `DurableTaskGrainRuntime.SetResponseAsync` stores the result if not already present and writes state
  - `src/Orleans.Runtime/DurableTasks/DurableTaskGrainRuntime.cs:225-264`

“Reply-to” is modeled as a set of observers:

- `TrySubscribeClient` stores an `IDurableTaskObserver` in task state
  - `src/Orleans.Runtime/DurableTasks/DurableTaskGrainRuntime.cs:73-85`
- `NotifyClientsAndCleanupTask` calls `client.OnResponseAsync(taskId, response)`
  - `src/Orleans.Runtime/DurableTasks/DurableTaskGrainRuntime.cs:272-321`

So the durable RPC reply path is effectively:
- caller provides an addressable callback interface
- callee stores it durably and invokes it after response is persisted

This “reply-to via observer reference” pattern is directly relevant for designing a durable inbox/outbox envelope which can support request/response.

### 2) Existing patterns for non-generic extension multiplexing

Orleans has multiple examples of non-generic extensions which support multiple “message types” by using explicit routing parameters and internal dispatch.

#### Stream consumer extension: routes by subscription id

- Interface: `IStreamConsumerExtension` (non-generic)
- Methods accept `object item` plus a `subscriptionId`

See `src/Orleans.Streaming/Internal/IStreamGrainExtensions.cs` (pattern documented by prior research).

The implementation uses `subscriptionId` to locate a handler in a dictionary.

This is a strong precedent for:
- non-generic delivery surface
- runtime type-checking or internal generic wrappers
- routing/demux by an identifier

#### Transaction extensions: routes by resource id

- `ITransactionManagerExtension` and `ITransactionalResourceExtension` accept `string resourceId`
- Implementation uses a dictionary keyed by `resourceId` to find the correct manager/resource instance

This is a precedent for a durable inbox which routes by something like:
- `channelId`, `protocolId`, `scopeId`, etc.

#### Broadcast channel extension: routes by stream id and type checks

- `IBroadcastChannelConsumerExtension.OnPublished(InternalChannelId streamId, object item)`
- Maintains handlers per stream id and type-checks `item` against the expected handler type

This is a precedent for an inbox receiving “polymorphic” messages (`object body`) then type-checking in handler wrappers.

### 3) Existing polymorphic envelope: DurableChannel (disabled)

The experimental `DurableChannel.cs` already models inbox/outbox as envelopes with a polymorphic `object` body.

- `src/Orleans.Journaling/Messaging/DurableChannel.cs:26-56` defines:
  - `InboxMessage` with `SenderId`, `MessageId`, `object MessageBody`, `RequestContext`
  - `OutboxMessage` with `ReceiverId`, `MessageId`, `object MessageBody`, `RequestContext`

It also defines a **non-generic grain extension interface**:

- `src/Orleans.Journaling/Messaging/DurableChannel.cs:58-61`
  - `internal interface IDurableMessageChannelGrainExtension : IGrainExtension`
  - `ValueTask AddToInboxAsync(InboxMessage message, CancellationToken cancellationToken);`

This matches the “avoid generic grain extension methods” constraint.

### 4) Where backpressure fits in Orleans patterns

There is an Orleans-native idiom for backpressure-like signaling even when the delivery is via grain calls:

- For streaming, delivery includes a handshake token and can implicitly slow down producers/transport based on consumer readiness (see `StreamHandshakeToken` usage in `IStreamConsumerExtension`).
- For Orleans calls generally, `RejectionTypes.Overloaded` is a system-level overload signal.

For a durable inbox/outbox interface, the closest codebase “shape” is `System.Threading.Channels`-style:
- `TryWrite` / `WaitToWriteAsync` / `WriteAsync`

While Orleans doesn’t expose that directly for extensions, the *interface design* can incorporate equivalent result codes.

### 5) Message type deliverability: how Orleans ensures an extension can receive a message

Orleans resolves extension implementations by interface type and installs them automatically if registered.

The relevant mechanism:

- An extension interface call (like `IDurableTaskGrainExtension.ScheduleAsync(...)`) is routed to the target activation.
- The activation resolves the extension instance via `GetComponent(typeof(TExtensionInterface))`.
- If the extension is not installed, Orleans attempts to auto-install it from a keyed DI registration.

See the extension auto-install pattern documented in:
- `src/Orleans.Runtime/Hosting/HostingGrainExtensions.cs` (registration helper)
- `src/Orleans.Runtime/Catalog/ActivationData.cs:856-872` (resolution)

From the durable inbox/outbox perspective:
- “deliverability of a type” is primarily:
  1. can the target activation resolve the inbox extension implementation?
  2. can the message body be deserialized?
  3. does the extension’s internal routing accept that message kind?

With a polymorphic `object MessageBody`, (2) relies on Orleans serialization having a codec for the runtime type.

## Architecture Documentation (what exists today)

### A. DurableTask already resembles durable inbox/outbox RPC

DurableTask request/response resembles:
- **outbox send**: caller sends a request envelope (`IDurableTaskRequest`) to target extension
- **inbox persist/execute**: target persists request before invoking
- **outbox reply**: target notifies observers (reply-to) after persisting response

Relevant files:
- `src/Orleans.Core.Abstractions/DurableTasks/DurableTaskRequest.cs:154` (ScheduleAsync to extension)
- `src/Orleans.Runtime/DurableTasks/DurableTaskGrainRuntime.cs:104` (durable scheduling)

### B. Codebase precedents for multiplexed, non-generic delivery

Relevant extension patterns:
- streaming (subscription-id routing)
- transactions (resource-id routing)
- broadcast channel (id routing + runtime type checks)

These patterns demonstrate how a single non-generic extension interface can support multiple logical message types.

## Historical Context (from research/)

- `research/docs/2026-01-15-inbox-outbox-api-design.md` describes an early proposal using generic inbox/outbox interfaces and generic extension; this follow-up focuses on codebase patterns which support the non-generic + polymorphic constraints.
- `research/docs/2026-01-15-durable-tasks-journaling-architecture.md` documents `DurableChannel.cs` and `IStateMachineManager` atomicity, which enable inbox/outbox + grain state to be flushed atomically.

## Related Research

- `research/docs/2026-01-15-inbox-outbox-api-design.md`
- `research/docs/2026-01-15-durable-tasks-journaling-architecture.md`

## Open Questions

These are questions which are *not fully answered by existing code* and would need further design work beyond documenting current behavior:

1. How should a single inbox/outbox support multiple message types while retaining strong typing ergonomics (likely via internal handler registries like streaming/broadcast channels)?
2. How should backpressure be expressed for inbox enqueue operations (result codes vs rejection exceptions vs asynchronous waiting)?
3. Should message routing be based on explicit `routeId` (subscription/resource id style), on CLR `Type`, or both?
4. How should “deliverability” be surfaced: do we require that a target grain explicitly registers which routes/types it accepts, or rely on runtime resolution failing with a known exception?
