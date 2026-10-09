# Microsoft Orleans Durable Messaging

This intermediate project supplies opaque durable message contracts, owned-payload codecs, and
journaled inbox processing.

`DurableEnvelope` carries application-supplied `HierarchicalKey MessageId`, `SenderId`, `ReceiverId`,
ordinal `Subject`, and an owned `ArcBuffer` payload.
The exact message identity supplies receiver-local deduplication across senders and subjects.
Applications namespace independent commands and preserve each command's destination, subject, and
body across resubmissions. Exact-key completion affects that command independently of parents
and children. Admission validates up to 1,024 UTF-8 bytes/32 segments per canonical key and
256 UTF-8 bytes per nonempty subject. Applications define payload formats, dispatch, and replies.
Dispose each owned envelope;
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
message with the logical generation and exact returned physical job handle. A pending duplicate
compares ordinal subject and payload bytes while permitting a changed immediate sender; conflicting
commands fail before scheduling or mutation and preserve the original pending envelope.
A retained completion acknowledges the existing outcome without retaining or comparing its original
body. Parent and child identifiers complete independently; hierarchy supplies deterministic identity
construction rather than prefix deduplication. Capacity is checked before admission. Handler failures during
local preparation follow bounded retry and dead-letter policy. `Complete()` stages removal and
deduplication synchronously beside safe business changes and outgoing intents. Errors after
completion preserve that logical outcome through the owned write and are reported after its ACK.

Admission retains the borrowed envelope before its first suspension and keeps that owner through
the actual operation independently of caller-wait cancellation. Standard durable dictionaries
own retained values. Each selected handler envelope has an independent pin through the actual
method outcome and persistence, so removing the dictionary entry during `Complete()` preserves
the handler's borrow. Recovery, reset, dead-letter removal, and scope disposal release the owners
at their respective boundaries.

Applications preserve the same deterministic command key, subject, and parameters across retries and
forwarding. Independent commands use distinct keys, and recipient-specific child keys identify fan-out
messages. Completion records cover the configured resubmission horizon; expiry permits the same key to
be accepted again. Typed decoding and subject dispatch remain application/composition concerns.

Reusable, noninterleaving timers carry immutable owner-bound work and a physical registration
generation. Stop closes admission and drains actual operations; full deletion then awaits the
advanced journal owner's deletion before disposal or deactivation. Subsequent use creates a
fresh owner. Actual storage failures retain their first cause and recover the persisted outcome.

`HierarchicalKey` is a readonly ordinal value with one immutable canonical backing path and a
cached process-local hash. `Create` and `CreateChildKey` accept literal segments; `Parse` reads an
escaped canonical path. `Append` composes built hierarchies. `default` is an unset identity.
Serialization stores canonical paths and reconstructs hash/navigation state. Shared journal value
lifecycles retain and release pending-message and dead-letter payloads at actual ownership boundaries.
Owned RPC arguments remain retained through their actual serialization and invocation outcomes.

This project remains non-packable while runtime and hosting layers assemble the eventual
`Microsoft.Orleans.DurableMessaging` package.

Completion records supply command deduplication during their configured retained lifetime.
A completed duplicate acknowledges the existing outcome and leaves application results in
business state or the original reply intent. After retention expiry the same identity can be
accepted again. Pending outbox restaging compares identity, sender, destination, ordinal subject,
and bytes; pending inbox comparison permits a changed immediate sender for the same command.
