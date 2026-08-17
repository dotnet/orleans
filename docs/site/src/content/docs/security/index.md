---
title: Orleans security
description: Understand Orleans trust boundaries and divide security responsibilities between Orleans, the application, and the hosting platform.
ms.date: 08/17/2026
ms.topic: overview
---

# Orleans security

Orleans provides transport security, serializer type controls, and grain-call filters that applications can configure to enforce policy. The application and hosting platform authenticate users and workloads, authorize operations, protect network paths and dependencies, manage secrets, and isolate tenants.

Every process with access to a gateway operates as an Orleans client within the cluster trust boundary. A connected client with compatible grain contracts can create grain references for known grain types and keys and invoke their interface methods. A grain reference addresses a grain, and application authorization grants access to its operations.

## Security boundaries

| Boundary | Orleans behavior | Application or platform responsibility |
|---|---|---|
| Public ingress to an application host | HTTP, gRPC, and other public protocols provide the application-facing boundary before Orleans calls begin. | Authenticate callers, validate input, rate limit requests, and expose the intended application operations. |
| Orleans client to silo gateway | The client sends grain calls over the Orleans protocol. TLS can encrypt the connection and authenticate workloads. | Restrict which workloads can reach gateway ports. Authenticate and authorize calls at a trusted boundary. |
| Silo to silo | Silos exchange grain calls, membership probes, directory traffic, and runtime coordination. TLS can protect the Orleans connection. | Admit members of the intended cluster, isolate silo ports, and protect certificate trust and private keys. |
| Host to provider | Orleans providers connect to membership, storage, reminder, and stream systems using provider-specific clients. | Configure provider authentication, TLS, network access, least privilege, backup, and auditing. |
| Dashboard and administrative endpoints | The dashboard is an ASP.NET Core endpoint whose access policy follows the host's routing and middleware configuration. | Require operator authentication and authorization, HTTPS, and administrative network isolation. |

<xref:Orleans.Configuration.ClusterOptions.ServiceId> and <xref:Orleans.Configuration.ClusterOptions.ClusterId> separate deployments logically. Provider access, network policy, and TLS identity control which processes can join or connect.

## Threat model

A production design should account for:

- An unauthorized workload reaching a gateway, silo, provider, or administrative port.
- A connected Orleans client invoking a method or grain key that its user wasn't intended to access.
- Forged caller-supplied identity, role, or tenant values.
- Serialized input selecting more CLR types than the application intended.
- Credentials, connection strings, certificates, grain state, or logs being disclosed.
- A compromised silo, client, provider account, or certificate acting with all permissions granted to that identity.
- Resource exhaustion from excessive connections, calls, message sizes, hot grain keys, or expensive operations.

Grains and tenants within a silo share its process security context. Grain code can use the process identity and services available to the silo. Separate processes, clusters, accounts, and network boundaries provide isolation between workloads.

## Start with a secure design

1. Terminate public traffic at an application-controlled HTTP, gRPC, or messaging boundary, and limit silo and gateway reachability to approved workloads.
1. Authenticate the caller at that boundary and authorize each operation against application resources. Use [grain call filters](authentication-authorization.md) to enforce the validated identity and policy consistently.
1. Restrict client, silo, provider, and administrative network paths according to [network hardening guidance](networking.md).
1. Configure [Orleans TLS](../host/transport-layer-security.md) when the transport crosses a shared or untrusted network. Configure provider TLS for each dependency connection.
1. Keep the serializer type surface narrow and validate deserialized application data. See [serialization security](serialization.md).
1. Use workload identities or protected secret stores and grant every provider identity least privilege. See [secrets and credentials](secrets.md).
1. Protect the [Orleans Dashboard](../dashboard/index.md) as an administrative endpoint.

Security controls should fail closed when identity or policy data is missing, invalid, or unavailable. Exercise authentication failures, authorization denials, certificate rotation, credential expiry, and dependency isolation before production traffic is admitted.

## In this section

- [Client and grain-call security](authentication-authorization.md)
- [Serialization security](serialization.md)
- [Secrets and credentials](secrets.md)
- [Network hardening](networking.md)
- [Secure Orleans connections with TLS](../host/transport-layer-security.md)
- [Secure the Orleans Dashboard](../dashboard/index.md#secure-the-dashboard-before-exposing-it)
