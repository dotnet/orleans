---
title: Message transport middleware
description: Decorate Orleans message transport connectors and listeners with cross-cutting connection behavior.
ms.date: 08/21/2026
ms.topic: how-to
---

# Message transport middleware

Orleans establishes silo-to-silo and client-to-gateway connections through message transport connectors and listeners. Transport middleware decorates those components before they create or accept a connection.

<xref:Orleans.Connections.Transport.IMessageTransportConnectorMiddleware> applies to outbound connectors. <xref:Orleans.Connections.Transport.IMessageTransportListenerMiddleware> applies to inbound listeners. Orleans applies every registered middleware instance in dependency-injection registration order.

Orleans uses transport middleware to apply TLS. See [Secure Orleans connections with TLS](transport-layer-security.md) for the built-in security configuration.

## Register middleware

Register connector and listener middleware as singleton services:

:::code language="csharp" source="./snippets/connection-middleware/ConnectionMiddlewareExamples.cs" id="RegisterMiddleware":::

The middleware instances can be invoked for multiple connectors or listeners, so keep per-connection state in the returned decorator or the resulting <xref:Orleans.Connections.Transport.MessageTransport>.

## Decorate outbound connectors

A connector middleware receives the next <xref:Orleans.Connections.Transport.MessageTransportConnector> and returns the connector Orleans should use. The decorator can observe connection attempts, select another transport, or wrap the returned message transport:

:::code language="csharp" source="./snippets/connection-middleware/ConnectionMiddlewareExamples.cs" id="ConnectorDecorator":::

Delegate <xref:Orleans.Connections.Transport.MessageTransportConnector.Features>, `IsValid`, and disposal unless the middleware deliberately changes those guarantees.

## Decorate inbound listeners

A listener middleware receives the next <xref:Orleans.Connections.Transport.MessageTransportListener> and returns the listener Orleans should bind and accept from:

:::code language="csharp" source="./snippets/connection-middleware/ConnectionMiddlewareExamples.cs" id="ListenerDecorator":::

Preserve the listener name so Orleans can select the silo and gateway listeners by role. Return `null` from `AcceptAsync` when the inner listener has stopped, matching the listener contract.

## See also

- <xref:Orleans.Connections.Transport.MessageTransport>
- <xref:Orleans.Connections.Transport.MessageTransportConnector>
- <xref:Orleans.Connections.Transport.MessageTransportListener>
- [Secure Orleans connections with TLS](transport-layer-security.md)
- [Server configuration](configuration-guide/server-configuration.md)
- [Client configuration](configuration-guide/client-configuration.md)
