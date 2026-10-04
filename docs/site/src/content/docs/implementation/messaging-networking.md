---
title: Transport and networking internals
description: Explain how Orleans maintains silo connections, frames messages, and handles transport failure and shutdown.
ms.date: 08/11/2026
ms.topic: concept-article
---

# Transport and networking internals

Orleans separates message routing from transport management. `MessageCenter` decides where a message should go; `ConnectionManager` obtains a usable connection to the target silo; the connection implementation batches and frames messages into requests for a message transport. This separation lets routing repair activation addresses while the transport manages reads, writes, and completion callbacks.

## Connection lifecycle

For each remote `SiloAddress`, `ConnectionManager` keeps a `ConnectionEntry` containing active connections, a pending connection attempt, and the last failure time. `GetConnection` reuses a suitable active connection. When active connections are exhausted, it starts one shared pending attempt per endpoint, and concurrent senders await that attempt.

Connection establishment has a bounded `OpenConnectionTimeout`. A failed attempt clears the pending task, removes defunct connections, records the failure, and applies the configured retry delay before another attempt. A timeout classifies the transport attempt as failed; application processing outcome requires separate reconciliation.

Source: [`ConnectionManager`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Core/Networking/ConnectionManager.cs), [`Connection`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Core/Networking/Connection.cs), and [`SiloConnection`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Networking/SiloConnection.cs).

The TCP transport enables keep-alive on outbound and accepted sockets. By default, the operating system sends the first probe after 90 seconds of idle time, then probes every 30 seconds and allows 10 unanswered probes before terminating the connection. Accepted sockets use the current options for their named listener, so each silo or gateway listener applies its configured keep-alive policy to new connections. When the platform reports an unsupported keep-alive option, the transport logs that option and retains supported settings; tuning continues for the other supported options.

## Message path and framing

When `MessageCenter.SendMessage` has a target silo, it reuses an existing connection or obtains one from `ConnectionManager`. A local target uses receive processing directly. A known-dead target causes a transient rejection for request and one-way messages, and expired messages are dropped before consuming transport work.

The connection pipeline performs the protocol preamble and then exchanges framed payloads. `MessageSerializer` encodes the message header and body using Orleans serialization. `MessageReadRequest` validates frame lengths before consuming the header and body. The body remains buffered until deserialization, forwarding, or disposal releases it. TLS decorates the message transport while preserving Orleans framing and the message protocol.

The connection accepts outgoing messages through an unbounded queue. Its send worker batches messages into write requests, and the transport writer observes flow control while concurrent senders continue adding messages to the connection queue. A slow connection can therefore accumulate queued messages until it recovers or closes. A successful write completes the request and releases its buffered message bodies; the grain result confirms application request completion. Disconnects remove the connection from the endpoint entry and cause later sends to establish a replacement.

## Failure and shutdown

When a connection terminates, failed write requests and queued send work reroute their messages through the connection-specific `RetryMessage` implementation. That implementation returns messages to `MessageCenter` for routing up to the retry limit, then rejects requests and drops other messages. A partially written request can have reached its target before failure, so applications reconcile ambiguous outcomes using their delivery and idempotency requirements.

Shutdown first blocks new application traffic while allowing responses and membership traffic needed to complete the stop protocol. `MessageCenter` rejects or drops blocked messages, stops accepting client messages, and then closes connections. Inbound application requests arriving at a stopping silo receive transient rejections with targeted cache invalidation, allowing senders to remove routes to that silo. Other inbound application messages are dropped, and their buffered bodies are released.

`ConnectionManager` closes establishment admission before canceling pending attempts and signaling existing connections to close. Each admitted producer remains owned until it publishes its connection or completes failure cleanup, including its connection runner. Shutdown then closes any late publications and drains the remaining outbound runners through middleware cleanup. The shutdown cancellation source stays valid until those operations finish; a canceled host stop preserves it for operations still unwinding. Inbound listeners stop accepting connections and close their tracked connections before awaiting the manager's closed signal and disposing their transport listener.

Connection closure stops send-work admission and closes the message transport to release pending TLS handshakes, preamble exchange, and transport I/O. After the connection runner and send worker quiesce, transport disposal releases the decorated transport chain. A connection closed before its queued runner starts completes transport shutdown before protocol initialization.

## Design trade-offs

- Per-endpoint connection state avoids global coordination but means every silo must observe and repair its own broken paths.
- Reusing connections reduces handshake and allocation cost, while multiple connections can improve throughput and avoid head-of-line blocking.
- Dropping expired messages protects a saturated runtime from work whose callback deadline has elapsed. Applications use operation IDs and durable state to reconcile the operation's outcome.

For end-to-end semantics, see [messaging and delivery semantics](messaging-delivery-guarantees.md). Network endpoint selection and firewall requirements belong in [topology, networking, and clustering](../deployment/networking.md).
