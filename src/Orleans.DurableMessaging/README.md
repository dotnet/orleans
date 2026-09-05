# Orleans durable messaging

`Microsoft.Orleans.DurableMessaging` provides journaled inbox, outbox, and
scheduled-message primitives for Orleans grains.

## Delivery boundary

An accepted envelope becomes part of the receiver's durable inbox. Handler
state changes, outgoing envelopes, inbox removal, and the processed-message
record complete at one journal commit boundary. Recovery either replays the
unprocessed inbox item or observes all handler effects and the completed inbox
record.

Message identifiers make delivery and completion idempotent. Handlers can still
call external systems, so those effects use application operation identifiers
or another idempotency protocol.

## Polling and cancellation

Long polling observes processing. A poll timeout returns `Pending` while the
durable handler continues. Caller cancellation stops the wait and does not
cancel or revert durable processing.

Service shutdown and explicit durable cancellation use separate runtime-owned
cancellation tokens.

## Isolation

Inbox handlers and state-mutating durable job handlers execute under an
activation-level isolation gate. The gate applies on reentrant grains, grains
with `MayInterleave` predicates, and call-chain-reentrant requests.

## Routing

Handlers are evaluated in registration order. Registering a replacement for an
existing legacy route changes the handler dispatched for that route without
changing the order of other registrations.

## Buffer ownership

Each persisted envelope owns its serialized buffer. Dead-lettering transfers
that ownership to the dead-letter record. Removal, replacement, recovery reset,
and replay release the final owner exactly once.

## Status

The package is an alpha feature and can change before a stable release.
