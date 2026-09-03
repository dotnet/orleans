# Efficient Broadcast for Orleans Runtime State

## Status and scope

This document describes the efficient-broadcast implementation introduced by #10236. It is an
internal silo-to-silo dissemination path for runtime state. It does not replace the authoritative
membership or manifest protocols.

The implementation currently carries two namespaces:

- deployment-load statistics, routed only through Active silos; and
- membership snapshots and repairs, routed through Joining, Active, ShuttingDown, and Stopping
  silos.

## Namespace-owned state and repair

An `IDisseminationNamespace` owns its current versions, any history needed to repair an older peer,
serialization, payload limits, and application. Queue entries contain only `(namespace, key)`
identities. A peer pump asks the namespace to create a repair immediately before sending, so a
coalesced notification sends the latest repairable value instead of retaining a stale serialized
payload.

A repair request includes the peer's acknowledged baseline and hard item, batch-byte, and
payload-byte budgets. A namespace can report that the peer is current, produce a complete repair or
a bounded prefix, report that the requested state is unavailable, or report that no valid repair
fits. Full values start at version zero and can replace any baseline; deltas must form a contiguous
version chain.

Applying a received batch is isolated per value. A namespace exception, including an
`OperationCanceledException` unrelated to the caller's token, is logged and emitted as a rejected
apply result. Later values are still applied and locally held values are still forwarded. Caller
cancellation is not contained and propagates to the caller.

## Acknowledgments and peer pumps

`PushBroadcast` is request/response. Its response reports the versions actually held by the receiver
and the namespaces which it does not support. A sender advances a peer's known-version ledger only
from this explicit evidence; accepting a queue notification is not delivery or support
confirmation.

Each peer has an independent pump which:

- coalesces repeated notifications by namespace and key;
- prioritizes high-priority namespaces;
- materializes repairs at send time;
- retries failed sends with bounded scheduling;
- caches the system-target reference; and
- drains independently, subject to the process-wide concurrent-send limit.

A blocked or failing peer therefore does not prevent another peer's pump from progressing.
Membership pruning removes obsolete peer state without discarding state which another namespace
scope still needs.

`MaxPendingItemCount` is a hard retained distinct-key limit for one namespace in one peer pump. An
already-retained key can still be updated at the limit. A new key is rejected with a bounded
diagnostic until acknowledgment-driven drain or membership pruning releases capacity. Serialized
item and byte limits are applied again when a batch is materialized.

## Deterministic topology and namespace scopes

Routing starts from one membership snapshot version. The membership cache projects that snapshot
into an all-eligible view and an Active-only view, avoiding separate source refreshes. Each
namespace selects its view before the deterministic originator and forwarding trees are built.

Deployment load never uses Joining, ShuttingDown, or Stopping silos as dissemination routes.
Membership retains those silos so that transitions can propagate while a silo is joining or
shutting down. Scope selection applies consistently to publication, forwarding, anti-entropy, and
peer-support queries.

## Bounded anti-entropy and fairness

Tree broadcast is accelerated by digest-based anti-entropy. A round contacts a bounded number of
eligible peers and observes response item limits, batch-byte limits, namespace payload limits, and
a bounded round lifetime. Recently advanced streams suppress redundant probes.

Response candidates use persistent per-peer cursors. When a response is truncated, the cursor
rotates so later eligible candidates are considered by subsequent exchanges instead of permanently
favoring the first keys or the first namespace scope. Non-member cursor retention is bounded and
pruned. Each request round uses one global peer cap and selects from the broadest participating
scope; Active-only namespace digests are attached only when the selected peer is Active.

## Mixed-version deployment-load delivery

Queue acceptance does not prove that a peer understands a namespace. Inbound namespace traffic,
broadcast acknowledgments, and authoritative anti-entropy responses provide support evidence;
explicit unsupported responses revoke it. Confirmation is per peer and namespace and remains valid
for that silo generation until the peer rejects the namespace or leaves the eligible membership set.

After a deployment-load update is accepted for dissemination, confirmed Active peers use
the dissemination path and unconfirmed Active peers still receive the existing direct system-target
update. If dissemination is disabled or unavailable, returns false, throws, or does not accept the
update within one deployment-load refresh interval, the publisher directly fans out to every Active
peer. This preserves delivery during rolling upgrades.

Load notifications to local listeners are coalesced latest-wins per silo while preserving stable
cross-silo order. Terminal removal is a tombstone: it dominates pending and concurrent updates and
prevents resurrection. One drainer invokes callbacks outside the statistics lock and continues
through later notifications even when a listener throws.

## Canonical manifest hashes and fallback authority

Manifest hashes use SHA-256 over a versioned canonical frame. Structural and field tokens delimit
manifest, grain, interface, property, collection, and entry boundaries. Counts and lengths are
big-endian integers. Grain and interface identifiers contribute their raw bytes. Property strings
contribute UTF-16 code units in big-endian order, preserving even invalid surrogate sequences.
Null and empty strings, and default and empty identifiers, have distinct tokens. Entries and
properties are sorted ordinally before hashing.

Cluster manifest peer fill is content-addressed: summaries name the expected per-silo hash, cached
content is reused by hash, and fetched content is accepted only after recomputing and matching that
hash. Peer probes are bounded and rotate across updates. Timeouts or invalid peer content do not
change authority: the provider falls back to the legacy direct silo-manifest fetch. Caller
cancellation stops peer fill and does not start a post-cancellation fallback.

## Authority boundaries

Direct membership gossip starts before optional membership dissemination and remains authoritative.
Dissemination failure or a late dissemination fault cannot suppress successful direct delivery, and
caller cancellation retains its original token and deadline behavior.

Likewise, dissemination and content-addressed peer fill only accelerate manifest convergence. The
legacy direct manifest RPC remains the authoritative fallback whenever hashes cannot be validated,
bounded probes do not produce content, or a peer does not support the newer contract.
