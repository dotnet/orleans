---
title: Network hardening
description: Restrict Orleans client, silo, provider, and administrative network paths.
ms.date: 08/12/2026
ms.topic: concept-article
---

# Network hardening

An Orleans deployment uses separate client-to-gateway, silo-to-silo, provider, application-ingress, and administrative network paths. Give each path only the reachability it needs. Endpoint configuration tells Orleans where to listen and what addresses to advertise; it doesn't create firewall, network-policy, or service-mesh rules.

The [topology and networking guide](../deployment/networking.md) explains listening and advertised endpoints. Apply the following security policy to those paths:

| Endpoint or dependency | Allow from | Don't expose to |
|---|---|---|
| Silo port | Silos which belong to the same intended cluster | Orleans clients, public ingress, and unrelated workloads |
| Gateway port | Approved Orleans client workloads | Browsers, mobile devices, public internet clients, and unrelated workloads |
| Application ingress | The application's intended callers through its ingress controls | Direct routes which bypass authentication, authorization, or rate limits |
| Membership, storage, reminder, and stream providers | Workloads and identities which use that provider | Unrelated clusters, environments, and public networks where private access is available |
| Dashboard and diagnostics | Authorized operators through an administrative path | Public and general application networks |

Silos communicate directly with the advertised address of every other silo. Don't put the silo endpoint behind a generic load balancer. Gateway discovery also returns individual gateway addresses, so client networks must reach those advertised endpoints.

## Harden cluster connectivity

- Don't expose silo or gateway ports to the public internet.
- Separate production, staging, development, and unrelated applications using network policy, provider namespaces, accounts, and distinct cluster identities.
- Remember that `ServiceId` and `ClusterId` are routing and data-separation values, not network admission or authentication controls.
- Restrict access to the clustering provider. A process which can read discovery data learns gateway and silo addresses; a process with write access can affect membership according to the provider operations it can perform.
- Preserve long-lived bidirectional TCP connections through firewalls, network address translation, and service meshes. Test policy changes during reconnect, rollout, and silo replacement.
- Apply connection, request, and resource limits at the public application boundary. Network isolation alone doesn't prevent an approved client from generating excessive grain calls or hot keys.

## Protect traffic in transit

Use [Orleans TLS](../host/transport-layer-security.md) for client-to-silo and silo-to-silo connections when the surrounding network isn't a sufficient trusted boundary. Prefer mutual TLS when both connecting workloads need certificate identity.

Orleans TLS protects only the Orleans transport. Configure TLS and server identity validation separately for clustering, grain storage, reminders, streams, telemetry, and other provider connections. A service mesh can protect traffic only when it supports Orleans' direct, long-lived TCP paths and preserves the advertised endpoint topology.

TLS doesn't authorize grain methods or tenants. Combine it with [application authentication and authorization](authentication-authorization.md), and keep certificate trust stores narrow enough that unrelated certificates aren't accepted as cluster identities.

## Isolate administrative surfaces

HTTP health endpoints, metrics exporters, profiling tools, and the Orleans Dashboard aren't carried over the Orleans silo or gateway ports. Protect each endpoint using its hosting stack.

<xref:Orleans.Dashboard.ServiceCollectionExtensions.MapOrleansDashboard*?displayProperty=nameWithType> doesn't require authentication by default. Put the dashboard behind HTTPS, operator authentication and authorization, and a private administrative path. See [secure the Orleans Dashboard](../dashboard/index.md#secure-the-dashboard-before-exposing-it).

## Verify the deployed boundary

From each network zone, test both allowed and denied paths:

1. Approved silos can reach every advertised silo endpoint; unrelated workloads can't.
1. Approved clients can reach advertised gateways; public and unrelated networks can't.
1. Hosts can reach only the provider endpoints their role requires.
1. TLS peers reject untrusted certificates, invalid names, and expired certificates.
1. Unauthenticated and unauthorized users can't reach dashboard or diagnostic data.
1. A rollout, address change, and connection retry don't bypass policy or depend on a previously open connection.
Record the deployed rules and their owners in the [production-readiness checklist](../deployment/production-readiness.md).
Record the deployed rules and their owners in the [production-readiness checklist](../deployment/production-readiness.md).
