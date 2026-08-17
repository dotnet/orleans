---
title: Transport and networking internals
description: Explain how Orleans maintains silo connections, frames messages, and handles transport failure and shutdown.
ms.date: 08/11/2026
ms.topic: concept-article
---

# Transport and networking internals

Orleans separates message routing from transport management. `MessageCenter` decides where a message should go; `ConnectionManager` obtains a usable connection to the target silo; the connection implementation frames, queues, and writes messages to a socket. This separation lets routing repair an activation address without making the transport responsible for grain placement.

## Connection lifecycle

For each remote `SiloAddress`, `ConnectionManager` keeps a `ConnectionEntry` containing active connections, a pending connection attempt, and the last failure time. `GetConnection` reuses an existing connection when one is suitable and starts at most one new attempt per endpoint when none is available. Concurrent senders await the same pending attempt instead of opening an unbounded connection storm.

Connection establishment has a bounded `OpenConnectionTimeout`. A failed attempt clears the pending task, removes defunct connections, records the failure, and applies the configured retry delay before another attempt. A timeout is therefore a transport failure, not evidence that the application message was processed.

Source: [`ConnectionManager`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Core/Networking/ConnectionManager.cs), [`Connection`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Core/Networking/Connection.cs), and [`SiloConnection`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Networking/SiloConnection.cs).

## Message path and framing

When `MessageCenter.SendMessage` has a target silo, it first uses an existing connection, otherwise it asks `ConnectionManager` for one. A local target loops back through receive processing without a socket. A known-dead target is rejected for requests and one-way messages rather than triggering a new connection attempt. Expired messages are dropped before they consume transport work.

The connection pipeline performs the protocol preamble and then exchanges framed payloads. `MessageSerializer` encodes the message header and body using Orleans serialization; the frame helper validates lengths and rejects malformed input before dispatch. TLS, when configured, is middleware around the connection rather than a different message protocol.

The connection accepts outgoing messages through an unbounded channel. Its socket writer observes transport flow control while concurrent senders can continue adding messages to that channel, so a slow connection can accumulate queued messages until it recovers or closes. A successful socket write means the frame was handed to the transport; the grain result confirms request completion. Disconnects remove the connection from the endpoint entry and cause later sends to establish a replacement.

## Failure and shutdown

When a connection terminates, the base connection sends each in-flight message to the connection-specific `OnSendMessageFailure` implementation and calls `RetryMessage` for messages which remain queued. Silo and gateway inbound connections fail in-flight messages; client outbound connections return them to `MessageCenter` for routing. Application-level response timeouts remain ambiguous because a request can have reached the target before the connection failed.

Shutdown first blocks new application traffic while allowing responses and membership traffic needed to complete the stop protocol. `MessageCenter` rejects or drops blocked messages, stops accepting client messages, and then closes connections. Inbound requests arriving at a stopping silo are rejected or dropped according to message direction, so callers must still handle a rejection or timeout.

## Design trade-offs

- Per-endpoint connection state avoids global coordination but means every silo must observe and repair its own broken paths.
- Reusing connections reduces handshake and allocation cost, while multiple connections can improve throughput and avoid head-of-line blocking.
- Dropping expired messages protects a saturated runtime from work whose callback can no longer complete, at the cost of losing a late response that might have been useful to an application retry.

For end-to-end semantics, see [messaging and delivery semantics](messaging-delivery-guarantees.md). Network endpoint selection and firewall requirements belong in [topology, networking, and clustering](../deployment/networking.md).
