# Microsoft Orleans Durable Messaging

This intermediate project supplies opaque durable message contracts, owned-payload codecs, and
journaled inbox processing.

`DurableEnvelope` carries `MessageId`, `SenderId`, `ReceiverId`, and an owned `ArcBuffer` payload.
The sender and message identifier form the transport deduplication key. Applications define payload
formats, dispatch, replies, ordering, and business-operation identity. Dispose each owned envelope;
`Retain()` acquires an independent payload lifetime. Serialization borrows its input, deserialization
transfers ownership to its result, and deep copying acquires an independent retained slice.

One nongeneric `IInboxHandler.HandleAsync` handles each inbox's messages. Its context exposes only
the borrowed envelope and synchronous `Complete()`. Decode and validate local values, perform
asynchronous work, construct outgoing envelopes, and check cancellation before the first shared
mutation. From that first mutation through method return, execute synchronously: apply complete
safe-to-commit business changes, send through an injected `IDurableOutbox`, call `Complete()`, and
return. This boundary is the handler's trusted responsibility. The runtime owns the subsequent
journal write, acknowledgement, and resource retirement. Logical completion coalesces within the
same active attempt; a retired or wrong-attempt completion is rejected.

`IDurableOutbox.Send` synchronously stages an envelope, borrowing the caller's payload while durable
state retains an independent slice. The journal capture hook establishes its self-wakeup before
capture; dispatch follows persistence acknowledgement. Inspection returns borrowed envelopes.
`IDurableInbox`, `IDurableInboxExtension`, `DeliveryResult`, `DeliveryStatus`, and `DurableInboxOptions`
define handler registration, delivery outcomes, capacity, retry, retention, and batch limits.

The inbox accepts an envelope after DurableJobs confirms its wakeup and the journal commits the
message with the logical generation and exact returned physical job handle. Retained duplicates
coalesce by `(SenderId, MessageId)`; capacity is checked before admission. Handler failures during
local preparation follow bounded retry and dead-letter policy. `Complete()` stages removal and
deduplication synchronously beside safe business changes and outgoing intents. Errors after
completion preserve that logical outcome through the owned write and are reported after its ACK.

Admission retains the borrowed envelope before its first suspension and keeps that owner through
the actual operation independently of caller-wait cancellation. Standard durable dictionaries
own retained values. Each selected handler envelope has an independent pin through the actual
method outcome and persistence, so removing the dictionary entry during `Complete()` preserves
the handler's borrow. Recovery, reset, dead-letter removal, and scope disposal release the owners
at their respective boundaries.

Reusable, noninterleaving timers carry immutable owner-bound work and a physical registration
generation. Stop closes admission and drains actual operations; full deletion then awaits the
advanced journal owner's deletion before disposal or deactivation. Subsequent use creates a
fresh owner. Actual storage failures retain their first cause and recover the persisted outcome.

`HierarchicalKey` is an application-level escaped, segment-aware key utility. Shared journal value
lifecycles retain and release pending-message and dead-letter payloads at actual ownership boundaries.
Owned RPC arguments remain retained through their actual serialization and invocation outcomes.

This project remains non-packable while runtime and hosting layers assemble the eventual
`Microsoft.Orleans.DurableMessaging` package.
