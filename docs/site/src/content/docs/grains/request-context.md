---
title: Request context
description: Flow application metadata with Orleans grain calls.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Request context

<xref:Orleans.Runtime.RequestContext> carries application metadata with an Orleans request. Typical values include correlation IDs, tenant IDs, and authorization context established by trusted application code.

:::code language="csharp" source="../snippets/compiled/Grains/RequestsAndVersioningSnippets.cs" id="set_request_context":::
The receiving grain reads the value:

:::code language="csharp" source="../snippets/compiled/Grains/RequestsAndVersioningSnippets.cs" id="read_request_context":::
Values must be serializable by Orleans. Keep them small because Orleans includes them in request messages.

## Propagation

Request context uses async-local storage. When code sends a grain call, Orleans copies the current entries into the outgoing request. The receiving grain sees those entries, and calls it makes propagate its current context onward.

Changes made by a callee don't flow back in the response. Treat request context as downstream metadata, not as a return channel.

Recoverable runtimes can persist a bounded request-context snapshot with durable work and restore it when that work resumes after activation or process restart. Orleans durable tasks retain up to 32 application entries, with 256-character keys, 64 KiB per serialized value, and 256 KiB in total. Scheduling fails when an application entry can't be serialized or exceeds these limits.

The restored values remain caller-provided metadata and follow the same serialization and security rules as an ordinary grain call. Polling a durable result doesn't replace the context captured when the work was scheduled. Orleans runtime markers, including call-chain reentrancy, ping, and activation turn-isolation state, remain scoped to the live call or activation and aren't persisted with durable work.

Use the static API to manage entries:

:::code language="csharp" source="../snippets/compiled/Grains/RequestsAndVersioningSnippets.cs" id="manage_request_context":::
Set context as close as possible to the operation that needs it, and restore or clear values before unrelated operations execute in the same asynchronous flow.

## Security

Request context is caller-provided data. Don't trust a role, user ID, or tenant ID merely because it arrived in `RequestContext`. Establish authentication at a trusted boundary and use call filters or application authorization logic to validate access.

See [client and grain-call security](../security/authentication-authorization.md) for identity propagation and authorization guidance.

## Placement and migration

Placement occurs before a new activation exists, so the static `RequestContext` isn't populated inside placement directors and filters. Read <xref:Orleans.Runtime.Placement.PlacementTarget.RequestContextData> instead.

When a grain requests migration using `MigrateOnIdle()`, Orleans captures the current request context and makes it available to placement. This allows an application to provide placement hints, but custom placement logic remains an advanced runtime extension.

## Call-chain reentrancy

<xref:Orleans.Runtime.RequestContext.AllowCallChainReentrancy> and `SuppressCallChainReentrancy` use request metadata internally to control scheduling for a call chain. Use their scoped return values with `using`; don't set Orleans-reserved context keys directly. See [Request scheduling](request-scheduling.md#call-chain-reentrancy).
