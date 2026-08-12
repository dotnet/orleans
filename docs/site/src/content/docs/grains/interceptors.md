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

A grain class can implement <xref:Orleans.IIncomingGrainCallFilter> to filter calls dispatched to that grain activation without affecting other grain classes:

:::code language="csharp" source="snippets/interceptors/GrainFilters.cs" id="per_grain_filter":::

The filter changes the result of `GetFavoriteNumber` from `7` to `38`. A grain-level filter doesn't need builder registration because Orleans discovers it on the grain instance. It also wraps grain extension calls dispatched through that activation, so select calls using `InterfaceMethod` or `ImplementationMethod` when the behavior isn't intended for extensions.

### Authorization with method attributes

A per-grain filter can enforce an application authorization policy declared by an attribute. This example evaluates an authenticated <xref:System.Security.Claims.ClaimsPrincipal> using an application authorization service:

:::code language="csharp" source="snippets/interceptors/GrainFilters.cs" id="access_control_contract":::

The grain receives the trusted identity accessor and authorization service through constructor injection, then checks the attribute before continuing the call:

:::code language="csharp" source="snippets/interceptors/GrainFilters.cs" id="access_control_grain":::

The attribute is on the grain implementation method, so the filter reads it from `ImplementationMethod`. Put attributes on the grain interface and inspect `InterfaceMethod` instead if the policy is part of the public contract.

The identity accessor must be implemented and populated by trusted host or filter infrastructure after it validates the caller's credentials. <xref:Orleans.Runtime.RequestContext> is application metadata that callers can set, and it flows transitively into grain calls made while handling the request. Treat its values as untrusted unless trusted infrastructure sets or validates them at each trust boundary. An Orleans client can set an `"isAdmin"` flag or claimed user ID, so those values alone aren't an authentication or authorization boundary. If a tamper-resistant credential is carried in request context, trusted infrastructure must still validate it before producing the authenticated principal. `SourceId` identifies an Orleans caller, not an authenticated end user.

See [client and grain-call security](../security/authentication-authorization.md) for the complete boundary and enforcement model.

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

Class-based filters are created by the dependency injection container, so they can receive <xref:Orleans.IGrainFactory> and make a guarded audit or telemetry call:

:::code language="csharp" source="snippets/interceptors/GrainFactoryInjection.cs" id="grain_factory_injection":::

Register the filter on the silo so Orleans can construct it and supply its dependencies:

:::code language="csharp" source="snippets/interceptors/Configuration.cs" id="register_grain_factory_filter":::

The nested grain call enters the normal outgoing filter pipeline on the current silo and the incoming filter pipeline on the target silo. A silo-wide incoming filter must exclude the audit target, as the sample does, or the audit call reenters the same filter and recurses indefinitely. Use an explicit target interface, grain type, method, or marker guard, and don't call the request's current target from its incoming filter.

Avoid call cycles. A non-reentrant grain remains busy while its filter awaits the nested call, so a downstream grain which calls back into that activation can deadlock. If a deliberate callback is unavoidable, use <xref:Orleans.Runtime.RequestContext.AllowCallChainReentrancy> only for the narrow call chain and account for interleaving; see [Call-chain reentrancy](request-scheduling.md#call-chain-reentrancy). The sample records an attempt before the original call and couples audit availability to the request: audit failure prevents `context.Invoke()` from running. The audit and original call aren't one transaction and aren't guaranteed exactly once, so a record can remain if the original call later fails or application-level retries can produce duplicates. Use an awaited grain call only when that coupling is intentional; prefer logging, tracing, or decoupled telemetry for observational work.

## Exceptions

Code before `context.Invoke()` runs on the way into the call. Code after it runs on successful return. Use `try`, `catch`, and `finally` around the awaited call to observe or transform failures.

If a filter catches an exception and doesn't rethrow, the exception is handled. Only replace exceptions when callers understand the replacement contract. Orleans already preserves unavailable remote exception details using <xref:Orleans.Serialization.UnavailableExceptionFallbackException>, so broad exception conversion is rarely necessary.

## Scope and ordering

Incoming filters also observe grain extension calls. Outgoing filters can observe Orleans system calls in addition to application calls. Filter by interface or method when behavior isn't intended globally.

Register silo-wide filters with <xref:Orleans.Hosting.GrainCallFilterSiloBuilderExtensions.AddIncomingGrainCallFilter*>. Class filters are singleton services created by the silo service provider, and their constructor dependencies are resolved from that provider. Grain-level filters have the grain activation's lifetime, and the usual grain activator supplies their constructor dependencies.

The incoming pipeline runs in this order:

1. Silo-wide filters, in registration order.
1. The grain-level filter, if the grain instance implements <xref:Orleans.IIncomingGrainCallFilter>.
1. The target grain or grain extension method.

Code before `context.Invoke()` follows that order. Code after the awaited call unwinds in reverse order.

Keep filters asynchronous, fast, and free of blocking calls. A filter adds latency to every call in its scope and can become a cluster-wide bottleneck.
