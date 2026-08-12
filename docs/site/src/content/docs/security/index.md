---
title: Orleans security
description: Understand Orleans trust boundaries and divide security responsibilities between Orleans, the application, and the hosting platform.
ms.date: 08/12/2026
ms.topic: overview
---

# Orleans security

Orleans is an application framework, not a security boundary by itself. It provides optional transport security, serializer type controls, and grain-call filters that applications can use to enforce policy. The application and hosting platform remain responsible for authenticating users and workloads, authorizing operations, protecting network paths and dependencies, managing secrets, and isolating tenants.

Treat every process that can connect as an Orleans client as a trusted workload unless the application has added and consistently enforced its own authorization protocol. A connected client with compatible grain contracts can create grain references for known grain types and keys and invoke their interface methods. A grain reference is an addressable proxy, not a capability token.

## Security boundaries

| Boundary | Orleans behavior | Application or platform responsibility |
|---|---|---|
| Public ingress to an application host | HTTP, gRPC, and other public protocols are separate from the Orleans transport. | Authenticate callers, validate input, rate limit requests, and expose only intended application operations. |
| Orleans client to silo gateway | The client sends grain calls over the Orleans protocol. TLS can encrypt the connection and authenticate workloads. | Restrict which workloads can reach gateway ports. Authenticate and authorize calls at a trusted boundary. |
| Silo to silo | Silos exchange grain calls, membership probes, directory traffic, and runtime coordination. TLS can protect the Orleans connection. | Admit only members of the intended cluster, isolate silo ports, and protect certificate trust and private keys. |
| Host to provider | Orleans providers connect to membership, storage, reminder, and stream systems using provider-specific clients. | Configure provider authentication, TLS, network access, least privilege, backup, and auditing. |
| Dashboard and administrative endpoints | The dashboard is an ASP.NET Core endpoint and has no authentication requirement by default. | Require operator authentication and authorization, HTTPS, and administrative network isolation. |

<xref:Orleans.Configuration.ClusterOptions.ServiceId> and <xref:Orleans.Configuration.ClusterOptions.ClusterId> separate deployments logically. They aren't credentials and don't prevent an unauthorized process from joining or connecting when it has provider and network access.

## Threat model

A production design should account for:

- An unauthorized workload reaching a gateway, silo, provider, or administrative port.
- A connected Orleans client invoking a method or grain key that its user wasn't intended to access.
- Caller-supplied identity, role, or tenant values being trusted without validation.
- Serialized input selecting more CLR types than the application intended.
- Credentials, connection strings, certificates, grain state, or logs being disclosed.
- A compromised silo, client, provider account, or certificate acting with all permissions granted to that identity.
- Resource exhaustion from excessive connections, calls, message sizes, hot grain keys, or expensive operations.

Orleans doesn't provide a sandbox between grains or tenants within a silo. Grain code runs in the silo process and can use the process identity and services available to it. Use separate processes, clusters, accounts, and network boundaries when workloads require isolation from one another.

## Start with a secure design

1. Terminate public traffic at an application-controlled HTTP, gRPC, or messaging boundary. Don't expose silo or gateway ports to the public internet.
1. Authenticate the caller at that boundary and authorize each operation against application resources. Use [grain call filters](authentication-authorization.md) for consistent enforcement, not as a substitute for authenticating the original caller.
1. Restrict client, silo, provider, and administrative network paths according to [network hardening guidance](networking.md).
1. Configure [Orleans TLS](../host/transport-layer-security.md) when the transport crosses a network that isn't already a trusted, isolated boundary. Configure provider TLS separately.
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
