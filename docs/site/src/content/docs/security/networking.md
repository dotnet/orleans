---
title: Network hardening
description: Restrict Orleans client, silo, provider, and administrative network paths.
ms.date: 08/17/2026
ms.topic: concept-article
---

# Network hardening

An Orleans deployment uses separate client-to-gateway, silo-to-silo, provider, application-ingress, and administrative network paths. Give each path the reachability it needs. Endpoint configuration tells Orleans where to listen and what addresses to advertise. Firewalls, network policies, and service meshes enforce reachability around those endpoints.

The [topology and networking guide](../deployment/networking.md) explains listening and advertised endpoints. Apply the following security policy to those paths:

| Endpoint or dependency | Allow from | Block from |
|---|---|---|
| Silo port | Silos which belong to the same intended cluster | Orleans clients, public ingress, and unrelated workloads |
| Gateway port | Approved Orleans client workloads | Browsers, mobile devices, public internet clients, and unrelated workloads |
| Application ingress | The application's intended callers through its ingress controls | Direct routes which bypass authentication, authorization, or rate limits |
| Membership, storage, reminder, and stream providers | Workloads and identities which use that provider | Unrelated clusters, environments, and public networks where private access is available |
| Dashboard and diagnostics | Authorized operators through an administrative path | Public and general application networks |

Silos communicate directly with the advertised address of every other silo, so route silo endpoints directly. Gateway discovery returns individual gateway addresses, so client networks must reach those advertised endpoints.

## Harden cluster connectivity

- Limit silo and gateway ports to approved private network paths.
- Separate production, staging, development, and unrelated applications using network policy, provider namespaces, accounts, and distinct cluster identities.
- Use `ServiceId` and `ClusterId` for routing and logical data separation. Use network policy and authenticated transport identity for admission.
- Restrict access to the clustering provider. A process which can read discovery data learns gateway and silo addresses; a process with write access can affect membership according to the provider operations it can perform.
- Preserve long-lived bidirectional TCP connections through firewalls, network address translation, and service meshes. Test policy changes during reconnect, rollout, and silo replacement.
- Apply connection, request, and resource limits at the public application boundary to bound the work an approved client can generate.

## Protect traffic in transit

Use [Orleans TLS](../host/transport-layer-security.md) for client-to-silo and silo-to-silo connections across shared or untrusted networks. Prefer mutual TLS when both connecting workloads need certificate identity.

Orleans TLS protects the Orleans transport. Configure TLS and server identity validation for clustering, grain storage, reminders, streams, telemetry, and other provider connections through their provider clients. A compatible service mesh preserves Orleans' direct, long-lived TCP paths and advertised endpoint topology while protecting traffic.

Server-authenticated TLS establishes the silo identity for clients, and mutual TLS establishes both transport peer identities. [Application authentication and authorization](authentication-authorization.md) maps those identities and any delegated user identity to permitted grain methods and tenants. Narrow certificate trust stores admit the intended cluster identities.

## Isolate administrative surfaces

HTTP health endpoints, metrics exporters, profiling tools, and the Orleans Dashboard use their hosting stack's endpoints. Protect each endpoint through that stack.

<xref:Orleans.Dashboard.ServiceCollectionExtensions.MapOrleansDashboard*?displayProperty=nameWithType> maps the dashboard into the ASP.NET Core request pipeline. Configure HTTPS, operator authentication and authorization, and a private administrative path in that pipeline. See [secure the Orleans Dashboard](../dashboard/index.md#secure-the-dashboard-before-exposing-it).

## Verify the deployed boundary

From each network zone, test both allowed and denied paths:

1. Approved silos can reach every advertised silo endpoint, and policy blocks unrelated workloads.
1. Approved clients can reach advertised gateways, and policy blocks public and unrelated networks.
1. Hosts can reach the provider endpoints their role requires.
1. TLS peers reject untrusted certificates, invalid names, and expired certificates.
1. Access policy denies unauthenticated and unauthorized requests for dashboard or diagnostic data.
1. Rollouts, address changes, and connection retries preserve policy enforcement.
Record the deployed rules and their owners in the [production-readiness checklist](../deployment/production-readiness.md).
