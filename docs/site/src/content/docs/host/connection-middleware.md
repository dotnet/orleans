---
title: Connection middleware
description: Plug custom logic, such as authentication or custom framing, into Orleans silo-to-silo and client-to-silo connections.
ms.date: 08/11/2026
ms.topic: how-to
---

# Connection middleware

Orleans silo-to-silo and client-to-gateway connections are built from a chain of middleware, similar to an ASP.NET Core request pipeline. <xref:Orleans.Runtime.Messaging.IConnectionMiddleware> lets you insert custom logic, such as authentication, custom framing, or connection-level diagnostics, into that pipeline without reimplementing connection setup.

Orleans itself uses this abstraction for TLS. See [Secure Orleans connections with TLS](transport-layer-security.md) for the built-in TLS middleware.

## The middleware interface

The interface defines `OnConnectionAsync(ConnectionContext, ConnectionDelegate)`. A middleware instance can be invoked concurrently for multiple connections, so store per-connection state in local variables or on the <xref:Microsoft.AspNetCore.Connections.ConnectionContext>, not on the middleware instance. Implementations should call `next(context)` to continue the pipeline after performing their work; not calling `next` terminates the connection.

## Register middleware

Use <xref:Orleans.ConnectionMiddlewareExtensions.UseMiddleware*> on an <xref:Microsoft.AspNetCore.Connections.IConnectionBuilder> to add middleware to a connection pipeline:

:::code language="csharp" source="./snippets/connection-middleware/ConnectionMiddlewareExamples.cs" id="RegisterMiddleware":::

Connection pipelines are configured through <xref:Orleans.Configuration.SiloConnectionOptions> (silo) and <xref:Orleans.Configuration.ClientConnectionOptions> (client). A silo has three distinct pipelines:

:::code language="csharp" source="./snippets/connection-middleware/ConnectionMiddlewareExamples.cs" id="SiloPipelines":::

An Orleans client has a single outbound pipeline, configured with <xref:Orleans.Configuration.ClientConnectionOptions>:

:::code language="csharp" source="./snippets/connection-middleware/ConnectionMiddlewareExamples.cs" id="ClientPipeline":::

Middleware added first runs first, wrapping every middleware added after it, matching the order `UseMiddleware` is called. Register the middleware type itself (for example `MyClientSideMiddleware`, `MyServerSideMiddleware`) as a singleton service when using the generic `UseMiddleware<T>()` overload.

> [!NOTE]
> Silo-to-silo and client-to-silo connections perform an Orleans-internal handshake after the connection pipeline runs. Middleware that terminates or replaces the transport (such as TLS) must leave a working `ConnectionContext` in place before calling `next`, because Orleans handshake and message framing execute afterward.

## Read and write framed data

Custom middleware that exchanges its own protocol data before calling `next` (for example, a handshake) can read and write directly from `context.Transport.Input`/`Output` (`PipeReader`/`PipeWriter`), or use <xref:Orleans.Runtime.Messaging.ConnectionFrameHelper> for structured, length-prefixed frames. `ConnectionFrameHelper` is optional; it exists to save middleware authors from re-implementing length-prefixed framing.

The wire format per frame is `[4-byte little-endian length][1-byte frame type][payload]`, where the length equals `1 + payload.Length`.

:::code language="csharp" source="./snippets/connection-middleware/ConnectionMiddlewareExamples.cs" id="ServerMiddleware":::

`ConnectionFrameHelper` also provides `WriteLengthPrefixedString`/`ReadLengthPrefixedString` helpers for encoding UTF-8 strings inside a frame payload, and a zero-copy `WriteFrameAsync` overload that writes the payload directly into the transport pipe buffer via an `Action<IBufferWriter<byte>>` delegate, avoiding an intermediate `byte[]` allocation.

`ReadFrameAsync` throws `InvalidOperationException` if the connection is closed mid-frame or if the declared frame length exceeds `maxFrameLength` (`ConnectionFrameHelper.DefaultMaxFrameLength`, 1 MB, by default). Pass a smaller `maxFrameLength` if your protocol's frames are bounded more tightly, to fail fast on malformed or hostile input.

## See also

- <xref:Orleans.Runtime.Messaging.IConnectionMiddleware>
- <xref:Orleans.Runtime.Messaging.ConnectionFrameHelper>
- [Secure Orleans connections with TLS](transport-layer-security.md)
- [Server configuration](configuration-guide/server-configuration.md)
- [Client configuration](configuration-guide/client-configuration.md)
