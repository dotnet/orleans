---
title: Connection middleware
description: Plug custom logic, such as authentication or custom framing, into Orleans silo-to-silo and client-to-silo connections.
ms.date: 08/10/2026
ms.topic: how-to
---

# Connection middleware

Orleans connections (silo-to-silo, client-to-gateway, and gateway-inbound) are built from a chain of middleware, similar to an ASP.NET Core request pipeline. <xref:Orleans.Runtime.Messaging.IConnectionMiddleware> lets you insert custom logic, such as authentication, custom framing, or connection-level diagnostics, into that pipeline without reimplementing connection setup.

Orleans itself uses this abstraction for TLS. See [Secure Orleans connections with TLS](transport-layer-security.md) for the built-in TLS middleware.

## The middleware interface

```csharp
public interface IConnectionMiddleware
{
    Task OnConnectionAsync(ConnectionContext context, ConnectionDelegate next);
}
```

A middleware instance can be invoked concurrently for multiple connections, so store per-connection state in local variables or on the <xref:Microsoft.AspNetCore.Connections.ConnectionContext>, not on the middleware instance. Implementations should call `next(context)` to continue the pipeline after performing their work; not calling `next` terminates the connection.

## Register middleware

Use `UseMiddleware` on an <xref:Microsoft.AspNetCore.Connections.IConnectionBuilder> to add middleware to a connection pipeline:

```csharp
// Resolves T from the application's dependency injection container.
// T must be registered as a singleton and must be safe for concurrent use.
builder.UseMiddleware<MyMiddleware>();

// Adds a shared instance. The caller owns the instance and is responsible for disposing it.
builder.UseMiddleware(new MyMiddleware(...));
```

Connection pipelines are configured through <xref:Orleans.Configuration.SiloConnectionOptions> (silo) and <xref:Orleans.Configuration.ClientConnectionOptions> (client). A silo has three distinct pipelines:

```csharp
siloBuilder.Configure<SiloConnectionOptions>(options =>
{
    // Connections this silo makes to other silos.
    options.ConfigureSiloOutboundConnection(connectionBuilder =>
    {
        connectionBuilder.UseMiddleware<MyClientSideMiddleware>();
    });

    // Connections this silo accepts from other silos.
    options.ConfigureSiloInboundConnection(connectionBuilder =>
    {
        connectionBuilder.UseMiddleware<MyServerSideMiddleware>();
    });

    // Connections this silo accepts from Orleans clients through the gateway.
    options.ConfigureGatewayInboundConnection(connectionBuilder =>
    {
        connectionBuilder.UseMiddleware<MyServerSideMiddleware>();
    });
});
```

An Orleans client has a single outbound pipeline, configured with <xref:Orleans.Configuration.ClientConnectionOptions>:

```csharp
clientBuilder.Configure<ClientConnectionOptions>(options =>
{
    options.ConfigureConnection(connectionBuilder =>
    {
        connectionBuilder.UseMiddleware<MyClientSideMiddleware>();
    });
});
```

Middleware added first runs first, wrapping every middleware added after it, matching the order `UseMiddleware` is called. Register the middleware type itself (for example `MyClientSideMiddleware`, `MyServerSideMiddleware`) as a singleton service when using the generic `UseMiddleware<T>()` overload.

> [!NOTE]
> Silo-to-silo and client-to-silo connections perform an Orleans-internal handshake after the connection pipeline runs. Middleware that terminates or replaces the transport (such as TLS) must leave a working `ConnectionContext` in place before calling `next`, because Orleans handshake and message framing execute afterward.

## Read and write framed data

Custom middleware that exchanges its own protocol data before calling `next` (for example, a handshake) can read and write directly from `context.Transport.Input`/`Output` (`PipeReader`/`PipeWriter`), or use <xref:Orleans.Runtime.Messaging.ConnectionFrameHelper> for structured, length-prefixed frames. `ConnectionFrameHelper` is optional; it exists to save middleware authors from re-implementing length-prefixed framing.

The wire format per frame is `[4-byte little-endian length][1-byte frame type][payload]`, where the length equals `1 + payload.Length`.

```csharp
public class MyServerSideMiddleware : IConnectionMiddleware
{
    public async Task OnConnectionAsync(ConnectionContext context, ConnectionDelegate next)
    {
        // Read one frame of the custom handshake protocol.
        var (frameType, payload) = await ConnectionFrameHelper.ReadFrameAsync(
            context, context.ConnectionClosed);

        // Validate the frame, e.g. an auth token, then respond.
        var responsePayload = Encoding.UTF8.GetBytes("ok");
        await ConnectionFrameHelper.WriteFrameAsync(
            context, frameType: 0x01, responsePayload, context.ConnectionClosed);

        // Continue the pipeline; Orleans's own handshake and framing run after this.
        await next(context);
    }
}
```

`ConnectionFrameHelper` also provides `WriteLengthPrefixedString`/`ReadLengthPrefixedString` helpers for encoding UTF-8 strings inside a frame payload, and a zero-copy `WriteFrameAsync` overload that writes the payload directly into the transport pipe buffer via an `Action<IBufferWriter<byte>>` delegate, avoiding an intermediate `byte[]` allocation.

`ReadFrameAsync` throws `InvalidOperationException` if the connection is closed mid-frame or if the declared frame length exceeds `maxFrameLength` (`ConnectionFrameHelper.DefaultMaxFrameLength`, 1 MB, by default). Pass a smaller `maxFrameLength` if your protocol's frames are bounded more tightly, to fail fast on malformed or hostile input.

## See also

- <xref:Orleans.Runtime.Messaging.IConnectionMiddleware>
- <xref:Orleans.Runtime.Messaging.ConnectionFrameHelper>
- [Secure Orleans connections with TLS](transport-layer-security.md)
- [Server configuration](configuration-guide/server-configuration.md)
- [Client configuration](configuration-guide/client-configuration.md)
