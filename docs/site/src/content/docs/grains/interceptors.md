---
title: Grain call filters
description: Intercept incoming and outgoing Orleans grain calls.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Grain call filters

Grain call filters run around Orleans calls. Use them for cross-cutting behavior such as authorization, telemetry, request metadata, and deliberate exception translation.

- <xref:Orleans.IIncomingGrainCallFilter> runs on the target silo before and after a grain or extension method.
- <xref:Orleans.IOutgoingGrainCallFilter> runs on a client or silo that sends a call.

Filters form an asynchronous pipeline. A filter must call and await `context.Invoke()` to continue to the next filter and eventually the target method.

## Incoming filters

The following compiled documentation sample logs completion and failure:

:::code language="csharp" source="snippets/interceptors/LoggingFilters.cs" id="logging_incoming_filter":::

Register it for the silo:

:::code language="csharp" source="snippets/interceptors/Configuration.cs" id="register_incoming_filter":::

A grain class can also implement `IIncomingGrainCallFilter` to filter calls to that grain only. Silo-wide incoming filters run in registration order, followed by the grain-level filter, and then the target method.

## Outgoing filters

Outgoing filters use the same pipeline pattern:

:::code language="csharp" source="snippets/interceptors/LoggingFilters.cs" id="logging_outgoing_filter":::

Register outgoing filters on silos or clients:

:::code language="csharp" source="snippets/interceptors/Configuration.cs" id="register_outgoing_filter":::

## Inspect and modify calls

<xref:Orleans.IGrainCallContext> exposes:

- `SourceId` and `TargetId`.
- `InterfaceType`, `InterfaceName`, and `MethodName`.
- `InterfaceMethod`.
- `Request`, an <xref:Orleans.Serialization.Invocation.IInvokable>.
- `Result` and `Response`.

Incoming contexts also expose `TargetContext` and `ImplementationMethod`; outgoing contexts expose `SourceContext`.

Use `context.Request.GetArgumentCount()`, `GetArgument(index)`, and `SetArgument(index, value)` to inspect or replace arguments. Current contexts and `IInvokable` don't expose an `Arguments` array.

The compiled delegate example demonstrates request context and result modification:

:::code language="csharp" source="snippets/interceptors/Configuration.cs" id="silo_delegate_filter":::

Changing arguments or results can violate interface expectations. Preserve declared types and apply transformations only to methods whose contract explicitly permits them.

## Exceptions

Code before `context.Invoke()` runs on the way into the call. Code after it runs on successful return. Use `try`, `catch`, and `finally` around the awaited call to observe or transform failures.

If a filter catches an exception and doesn't rethrow, the exception is handled. Only replace exceptions when callers understand the replacement contract. Orleans already preserves unavailable remote exception details using `UnavailableExceptionFallbackException`, so broad exception conversion is rarely necessary.

## Scope and ordering

Incoming filters also observe grain extension calls. Outgoing filters can observe Orleans system calls in addition to application calls. Filter by interface or method when behavior isn't intended globally.

Keep filters asynchronous, fast, and free of blocking calls. A filter adds latency to every call in its scope and can become a cluster-wide bottleneck.
