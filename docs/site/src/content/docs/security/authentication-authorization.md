---
title: Client and grain-call security
description: Authenticate callers at a trusted boundary and enforce application authorization for Orleans grain calls.
ms.date: 08/12/2026
ms.topic: concept-article
---

# Client and grain-call security

Orleans doesn't authenticate end users or apply an authorization policy to grain methods by default. Connecting to a gateway establishes an Orleans transport connection; it doesn't create a <xref:System.Security.Claims.ClaimsPrincipal> for grain calls. Applications must establish caller identity and enforce access to operations and resources.

## Keep untrusted callers outside the Orleans boundary

Prefer this request path:

1. An HTTP, gRPC, messaging, or other application endpoint authenticates the caller.
1. The endpoint validates input and authorizes the requested application operation.
1. Trusted host code calls the required grains through a co-hosted or external Orleans client.
1. Grain code repeats resource-level checks which depend on state owned by the grain.

This design exposes an application contract rather than the Orleans protocol and its full set of referenced grain interfaces. Use network policy so only approved application workloads can reach gateway ports.

If an Orleans client must run in a less-trusted tier, use defense in depth: restrict its network path, authenticate the workload with [mutual TLS](../host/transport-layer-security.md#configure-mutual-tls), carry a tamper-resistant application credential, and validate authorization before every protected operation. TLS workload identity doesn't automatically become grain-call identity, so the application must define that mapping and enforcement.

## Carry identity deliberately

<xref:Orleans.Runtime.RequestContext> propagates serializable application metadata with a call and with downstream grain calls. Any Orleans client can set its values. Therefore, a user ID, role, tenant ID, or `"isAdmin"` flag in request context is a claim from the caller, not proof of identity.

Safe patterns include:

- Authenticate at a trusted application boundary and have trusted code construct a minimal identity context for the immediate call.
- Carry a signed, short-lived credential and validate its signature, issuer, audience, lifetime, and operation-specific claims in trusted silo infrastructure.
- Resolve authorization data from a trusted store using an authenticated subject identifier instead of accepting caller-supplied roles or permissions.

Don't propagate raw passwords, provider credentials, long-lived bearer tokens, or a serialized `ClaimsPrincipal`. Keep identity metadata minimal, give it an explicit schema and lifetime, and clear or replace request context before unrelated work runs in the same asynchronous flow.

Request context propagates transitively. A downstream grain can't infer which upstream component originally validated a value. Revalidate identity when a call crosses another application trust boundary, and don't let an intermediate grain replace identity or tenant values with more privileged ones.

## Enforce authorization

Use <xref:Orleans.IIncomingGrainCallFilter> for policy that must run before grain or grain-extension methods. A silo-wide filter can enforce a common baseline; a grain class can implement the interface for policy specific to that grain. The filter must deny the call without invoking the next stage when authorization fails.

The [grain call filter guidance](../grains/interceptors.md#authorization-with-method-attributes) demonstrates policy attributes and an application-provided identity accessor. That accessor must be populated only after trusted infrastructure validates the caller's credential.

Authorization should consider both the operation and target resource:

- Check tenant or account ownership against trusted grain state, not only against a caller-provided grain key.
- Apply policy to every interface or method that can reach the protected state, including grain extensions and administrative methods.
- Default to denial when identity, tenant, policy, or required state is absent.
- Keep policy evaluation bounded and observable. A silo-wide filter runs on every call in its scope.
- Test direct grain calls, downstream grain calls, retries, and calls from other silos, not only the public endpoint.

`IGrainCallContext.SourceId` identifies the Orleans caller, which can be a grain or client instance. It isn't an authenticated end-user or workload identity and isn't sufficient to authorize a user. Grain type names, grain keys, `ServiceId`, and `ClusterId` are also not authentication factors.

## Separate transport identity from application authorization

[TLS](../host/transport-layer-security.md) can authenticate a connecting workload by certificate and protect bytes in transit. It doesn't decide whether that workload may invoke a grain method, act for a user, or access a tenant's grain. Conversely, an application authorization check doesn't encrypt the connection or authenticate the silo.

Use both layers when the network path requires them:

- Network policy and TLS decide which workloads can establish Orleans connections.
- Application authentication establishes the caller or service identity.
- Grain-call filters and grain logic authorize operations and resources.

For the broader boundary model, see [Orleans security](index.md).
