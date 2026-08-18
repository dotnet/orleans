---
title: Client and grain-call security
description: Authenticate callers at a trusted boundary and enforce application authorization for Orleans grain calls.
ms.date: 08/17/2026
ms.topic: concept-article
---

# Client and grain-call security

Connecting to a gateway establishes an Orleans transport connection for grain calls. Applications establish end-user identity, create any <xref:System.Security.Claims.ClaimsPrincipal> used by their policy, and enforce access to operations and resources.

## Keep untrusted callers outside the Orleans boundary

Prefer this request path:

1. An HTTP, gRPC, messaging, or other application endpoint authenticates the caller.
1. The endpoint validates input and authorizes the requested application operation.
1. Trusted host code calls the required grains through a co-hosted or external Orleans client.
1. Grain code repeats resource-level checks which depend on state owned by the grain.

This design exposes a purpose-built application contract and keeps the Orleans protocol and referenced grain interfaces inside the workload boundary. Use network policy so approved application workloads can reach gateway ports.

For an Orleans client in a less-trusted tier, use defense in depth: restrict its network path, authenticate the workload with [mutual TLS](../host/transport-layer-security.md#configure-mutual-tls), carry a tamper-resistant application credential, and validate authorization before every protected operation. Define how the authenticated TLS workload identity maps to grain-call identity and policy.

## Carry identity deliberately

<xref:Orleans.Runtime.RequestContext> propagates serializable application metadata with a call and with downstream grain calls. Any Orleans client can set its values, so treat a user ID, role, tenant ID, or `"isAdmin"` flag as a caller-provided claim that requires validation.

Safe patterns include:

- Authenticate at a trusted application boundary and have trusted code construct a minimal identity context for the immediate call.
- Carry a signed, short-lived credential and validate its signature, issuer, audience, lifetime, and operation-specific claims in trusted silo infrastructure.
- Resolve authorization data from a trusted store using an authenticated subject identifier.

Keep raw passwords and provider credentials in their secret-management systems. Carry minimal, short-lived identity metadata with an explicit schema, and clear or replace request context before unrelated work runs in the same asynchronous flow.

Request context propagates transitively and carries the values supplied by the upstream caller. Revalidate identity when a call crosses another application trust boundary, and preserve the validated identity and tenant values through intermediate grains.

## Enforce authorization

Use <xref:Orleans.IIncomingGrainCallFilter> for policy that must run before grain or grain-extension methods. A silo-wide filter can enforce a common baseline; a grain class can implement the interface for policy specific to that grain. When authorization fails, the filter denies the call and terminates the pipeline before the next stage.

The [grain call filter guidance](../grains/interceptors.md#authorization-with-method-attributes) demonstrates policy attributes and an application-provided identity accessor. Populate that accessor after trusted infrastructure validates the caller's credential.

Authorization should consider both the operation and target resource:

- Check tenant or account ownership against trusted grain state and use the grain key as a resource locator.
- Apply policy to every interface or method that can reach the protected state, including grain extensions and administrative methods.
- Default to denial when identity, tenant, policy, or required state is absent.
- Keep policy evaluation bounded and observable. A silo-wide filter runs on every call in its scope.
- Test direct grain calls, downstream grain calls, retries, calls from other silos, and the public endpoint.

`IGrainCallContext.SourceId` identifies the Orleans caller as a grain or client instance. Use a separately validated application identity to authorize an end user or workload. Grain type names, grain keys, `ServiceId`, and `ClusterId` identify routing and deployment resources.

## Separate transport identity from application authorization

[TLS](../host/transport-layer-security.md) protects bytes in transit. Server-authenticated TLS establishes the silo identity for clients, and mutual TLS establishes both workload identities. Application policy maps those identities to permitted grain methods, delegated users, and tenant resources.

Use both layers when the network path requires them:

- Network policy and TLS decide which workloads can establish Orleans connections.
- Application authentication establishes the caller or service identity.
- Grain-call filters and grain logic authorize operations and resources.

For the broader boundary model, see [Orleans security](index.md).
