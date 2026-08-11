---
title: Security in Orleans
description: Understand Orleans trust boundaries and secure the clients, silos, grain calls, data, and operational surfaces around a production deployment.
ms.date: 08/11/2026
ms.topic: overview
---

# Security in Orleans

Orleans provides a distributed application runtime: silos route grain calls, manage activations, and coordinate cluster membership. It is not an identity provider, a policy engine for application users, or a substitute for network and secret management. A secure deployment combines Orleans configuration with controls supplied by the hosting platform and the application.

Use this page as the security starting point. The linked pages describe the implementation details and configuration options; this page describes how they fit together.

## Trust boundaries

Treat each of these as a separate boundary and make its trust assumptions explicit:

| Boundary | What crosses it | Controls Orleans provides | Controls the application or platform must provide |
| --- | --- | --- | --- |
| Client to gateway | Grain calls, arguments, results, and request context | Gateway discovery and transport configuration | Which workloads may connect, transport encryption, caller authentication, and application authorization |
| Silo to silo | Grain calls, runtime messages, membership and directory traffic | Cluster protocols and endpoint configuration | Network isolation, TLS or an equivalent trusted network, certificate trust, and cluster admission |
| Silo to provider | Membership, grain state, reminders, streams, and telemetry | Provider integrations and serialization | Provider authentication, least privilege, encryption, private connectivity, and credential rotation |
| HTTP or dashboard to operator | Administrative pages, logs, metrics, and grain state | Dashboard endpoints and data collection | HTTPS, operator authentication and authorization, network restriction, redaction, and retention |
| Grain call to application code | Deserialized arguments, request metadata, and returned values | Version-tolerant serialization and call pipelines | Input validation, tenant isolation, authorization decisions, and protection of sensitive data after deserialization |

Any process permitted to connect to an Orleans transport endpoint is a workload in the cluster's trust model. A successful TLS handshake proves only the identity permitted by the certificate policy; it does not prove that an end user is authorized to invoke a grain method.

## Secure deployment path

1. **Define the entry point.** Prefer an authenticated application API or worker tier in front of Orleans. Keep direct gateway access limited to the workloads which need it.
2. **Isolate the cluster.** Allow silo ports only between intended silos and gateway ports only from intended clients. Never expose either port to the public internet.
3. **Authenticate workloads.** Use [Orleans TLS](../host/transport-layer-security.md) for connections across networks which are not already private and strongly isolated. Use mutual TLS when both sides need certificate-based workload identity.
4. **Authorize grain calls.** Establish the application user identity at a trusted boundary, then enforce policy in incoming call filters or in the grain/application layer.
5. **Constrain serialized types.** Keep the Orleans type allow-list narrow when serialized input can be influenced by a less-trusted workload. Do not enable `AllowAllTypes` for that traffic.
6. **Protect dependencies.** Use workload or managed identity where available, grant provider identities only the permissions they need, and rotate credentials and certificates.
7. **Protect operations.** Treat the [Orleans Dashboard](../dashboard/index.md), logs, metrics, and health endpoints as administrative data, not public application content.

Use the [production-readiness checklist](../deployment/production-readiness.md) to record the owner and verification method for each decision.

## Client and gateway exposure

External clients connect to gateway-enabled silos, while silo-to-silo traffic uses the silo endpoint. Gateway discovery does not authenticate an application user and a gateway port is not an API authorization boundary.

- Put an external client behind an application ingress, service identity, private network, or equivalent boundary when callers are not fully trusted.
- Configure the client and silos with the same service ID, cluster ID, and clustering provider, but do not make the clustering provider publicly reachable.
- Expose only the advertised gateway endpoints required by client networks. Do not advertise loopback, an unroutable address, or a shared load-balancer address for individual silo endpoints.
- Disable gateways on silos which must not accept external clients by setting the gateway port to `0`.

See [Client configuration](../host/configuration-guide/client-configuration.md), [Server configuration](../host/configuration-guide/server-configuration.md#endpoints), and [Topology, networking, and clustering](../deployment/networking.md).

## Authentication and authorization for grain calls

Orleans does not authenticate end-user identities or automatically authorize grain methods. A common pattern is:

1. Authenticate the caller at an HTTP, messaging, or workload boundary using the application's identity provider.
2. Create a trusted `ClaimsPrincipal` or equivalent application identity in the Orleans host.
3. Enforce the policy in an [incoming grain call filter](../grains/interceptors.md#authorization-with-method-attributes), or in the grain method when the rule is specific to the operation.
4. Pass only the minimum validated context needed by downstream grains.

`RequestContext` is serialized application metadata. A client can set a role, user ID, tenant ID, or an `"isAdmin"` value itself, so those values are not credentials. Validate them or replace them with trusted identity state at every trust boundary. See [Request context](../grains/request-context.md#security).

Outgoing filters can add consistency checks or telemetry, but they do not replace enforcement on the target silo. Every silo which can receive a call must apply the authorization policy, and grains must still validate resource ownership and input values.

## Serialization safety

Orleans deserializes grain-call arguments, request context, storage state, and stream payloads. Serialization is not a confidentiality boundary: callers with access to a call path can observe or influence data allowed by the contract, and deserialized values must be treated as input.

The default Orleans serializer requires known or explicitly allowed types. For polymorphic contracts or external serializers, configure the smallest set of allowed types or trusted assemblies with `TypeManifestOptions.AddAllowedType`, `AddAllowedAssembly`, or a type filter. `AllowAllTypes` bypasses type-name validation and is insecure when serialized input can be influenced by an untrusted party.

Do not put passwords, bearer tokens, private keys, or other secrets in grain arguments, request context, logs, dashboard-visible state, or durable grain state unless the application has a specific protection and retention design. Prefer purpose-built credential stores and short-lived references. See [Serialization configuration](../host/configuration-guide/serialization-configuration.md#authorize-type-name-resolution) and [Serialization in Orleans](../host/configuration-guide/serialization.md).

## Secrets and provider credentials

Orleans provider packages use the credentials supplied through their options and hosting environment; Orleans does not manage the lifecycle of those credentials.

- Prefer managed identity, workload identity, or the provider SDK's workload credential chain over long-lived access keys.
- Keep connection strings, certificate passwords, private keys, and tokens out of source control, container images, ordinary configuration files, and diagnostic output.
- Grant separate provider identities only the membership, storage, reminder, stream, or telemetry permissions they require.
- Rotate credentials before expiry and test rotation without accidentally broadening access.
- Restrict access to the clustering provider as carefully as access to the silo and gateway ports: membership data reveals cluster topology and endpoint information.

See [Server configuration](../host/configuration-guide/server-configuration.md#production-guidance), [Typical configurations](../host/configuration-guide/typical-configurations.md#production-configuration), and the provider-specific credential guidance in the [grain persistence](../grains/grain-persistence/index.md) and [streaming](../streaming/index.md) sections.

## Network hardening

Use private subnets, firewall rules, security groups, Kubernetes NetworkPolicies, or an equivalent control to enforce these paths:

- silo to silo: only the silos in the intended cluster;
- client to gateway: only the authenticated client workloads which need grain access;
- host to provider: only the deployment identities and destinations required by configured providers; and
- HTTP ingress: the public application protocol, kept separate from Orleans transport ports.

TLS protects Orleans transport traffic but does not secure provider connections, authorize grain calls, or make an exposed endpoint safe. Configure provider-specific encryption and authentication separately, and use [TLS](../host/transport-layer-security.md) when the network boundary is not sufficient on its own.

## Administrative surfaces

The [Orleans Dashboard](../dashboard/index.md) is an administrative surface and can expose grain keys, state, logs, topology, and runtime details. It does not require authentication by default. Put it behind HTTPS, operator authentication and authorization, and a restricted network path before mapping it in a shared environment.

For a complete operational review, combine this page with [Topology, networking, and clustering](../deployment/networking.md), [Secure Orleans connections with TLS](../host/transport-layer-security.md), and the [production-readiness checklist](../deployment/production-readiness.md).
