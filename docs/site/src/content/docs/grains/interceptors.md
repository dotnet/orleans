---
title: Grain call filters
description: Intercept incoming and outgoing Orleans grain calls.
ms.date: 08/08/2026
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

### Per-grain filters

A grain class can implement <xref:Orleans.IIncomingGrainCallFilter> to filter calls to that grain without affecting other grain classes:

:::code language="csharp" source="snippets/interceptors/GrainFilters.cs" id="per_grain_filter":::

The filter changes the result of `GetFavoriteNumber` from `7` to `38`. A grain-level filter doesn't need builder registration because Orleans discovers it on the grain instance. It also wraps grain extension calls dispatched through that activation, so select calls using `InterfaceMethod` or `ImplementationMethod` when the behavior isn't intended for extensions.

### Authorization with method attributes

A per-grain filter can enforce an application authorization policy declared by an attribute. Keep the identity and policy decision in a service supplied by trusted application infrastructure:

:::code language="csharp" source="snippets/interceptors/GrainFilters.cs" id="access_control_contract":::

The grain injects that service and checks the attribute before continuing the call:

:::code language="csharp" source="snippets/interceptors/GrainFilters.cs" id="access_control_grain":::

The attribute is on the grain implementation method, so the filter reads it from `ImplementationMethod`. Put attributes on the grain interface and inspect `InterfaceMethod` instead if the policy is part of the public contract.

The authorization service must base its decision on an identity or credential established and validated by trusted infrastructure. <xref:Orleans.Runtime.RequestContext> is caller-provided data: a client can set an `"isAdmin"` flag or claimed user ID, so such values aren't an authentication or authorization boundary. If trusted ingress code propagates identity through request context, protect its integrity and have the authorization service validate it. `SourceId` identifies an Orleans caller, not an authenticated end user.

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

## Call grains from a filter

Class-based filters are created by the dependency injection container, so they can receive <xref:Orleans.IGrainFactory> and call a grain:

:::code language="csharp" source="snippets/interceptors/GrainFactoryInjection.cs" id="grain_factory_injection":::

Register the filter on the silo so Orleans can construct it and supply its dependencies:

:::code language="csharp" source="snippets/interceptors/Configuration.cs" id="register_grain_factory_filter":::

A grain call made by a filter follows the normal outgoing and incoming pipelines. A silo-wide incoming filter must exclude the grain it calls, as the sample does, or the nested call reenters the same filter indefinitely. Don't call the request's current target from its incoming filter.

Avoid call cycles. A non-reentrant grain remains busy while its filter awaits the nested call, so a downstream grain which calls back into that activation can deadlock. Prefer an acyclic design. If a deliberate callback is unavoidable, use <xref:Orleans.Runtime.RequestContext.AllowCallChainReentrancy> only for the narrow call chain and account for interleaving; see [Call-chain reentrancy](request-scheduling.md#call-chain-reentrancy).

The sample awaits the audit grain before continuing, so audit failure prevents the original call. Use a synchronous grain call only when that coupling is intentional; prefer logging, tracing, or a decoupled telemetry path for observational work.

## Exceptions

Code before `context.Invoke()` runs on the way into the call. Code after it runs on successful return. Use `try`, `catch`, and `finally` around the awaited call to observe or transform failures.

If a filter catches an exception and doesn't rethrow, the exception is handled. Only replace exceptions when callers understand the replacement contract. Orleans already preserves unavailable remote exception details using `UnavailableExceptionFallbackException`, so broad exception conversion is rarely necessary.

## Scope and ordering

Incoming filters also observe grain extension calls. Outgoing filters can observe Orleans system calls in addition to application calls. Filter by interface or method when behavior isn't intended globally.

Register silo-wide filters with <xref:Orleans.Hosting.GrainCallFilterSiloBuilderExtensions.AddIncomingGrainCallFilter*>. Class filters are singleton services created by the silo service provider, and their constructor dependencies are resolved from that provider.

The incoming pipeline runs in this order:

1. Silo-wide filters, in registration order.
1. The grain-level filter, if the grain instance implements <xref:Orleans.IIncomingGrainCallFilter>.
1. The target grain or grain extension method.

Code before `context.Invoke()` follows that order. Code after the awaited call unwinds in reverse order.

Keep filters asynchronous, fast, and free of blocking calls. A filter adds latency to every call in its scope and can become a cluster-wide bottleneck.
