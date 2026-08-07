---
title: Topology, networking, and clustering
description: Configure Orleans endpoints, network paths, and cluster discovery for production.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Topology, networking, and clustering

An Orleans deployment has three distinct network paths:

| Path | Purpose | Required reachability |
| --- | --- | --- |
| Silo to silo | Membership probes, grain calls, directory traffic, and runtime coordination | Every silo to every advertised silo endpoint |
| Client to gateway | Grain calls from Orleans clients | Every client to the advertised gateway endpoints it discovers |
| Host to provider | Membership, grain state, reminders, streams, and telemetry | Each participating host to its configured dependencies |

HTTP ingress isn't an Orleans transport. A web API can share a process with a silo, but its HTTP port and load balancer are separate from the silo and gateway TCP endpoints.

## Listening and advertised endpoints

A **listening endpoint** is the local interface and port on which a process accepts connections. An **advertised endpoint** is the address and port stored in membership so other processes can connect.

These values can differ behind a container network, network address translation, or a hosting platform that assigns externally routed ports. Binding to `0.0.0.0` or `::` makes a process listen on available interfaces; it doesn't provide a usable address to advertise.

For a direct-address container platform such as Kubernetes:

- Listen on all container interfaces.
- Advertise the pod IP.
- Keep the configured silo and gateway ports equal to the container ports.

For a platform that maps private addresses or ports:

- Read the platform-provided routable address and mapped ports.
- Advertise those mapped values.
- Bind the listening endpoints to interfaces and ports that exist inside the process.
- Confirm every peer can connect to the advertised values. A successful local bind isn't sufficient.

Never advertise loopback, a host name that peers resolve differently, a load balancer virtual IP for the silo endpoint, or an ephemeral address that the clustering provider can retain after the instance is gone.

## Cluster identity

All silos and clients in one logical deployment must agree on:

- <xref:Orleans.Configuration.ClusterOptions.ServiceId>: The stable identity of the application. Grain storage providers can use it to separate applications.
- <xref:Orleans.Configuration.ClusterOptions.ClusterId>: The identity of a particular deployment environment or cluster.
- The clustering provider and its provider-specific namespace, database, table, or key prefix.

Don't use a shared production `ClusterId` for staging. During blue-green deployment, use different cluster IDs unless both versions are intentionally compatible members of one cluster.

## Choose a clustering provider

The clustering provider is a coordination dependency used for membership and gateway discovery. It isn't a repository of grain activation state and doesn't replace a grain storage provider.

Choose a provider already operated reliably in the target environment:

- Azure Table Storage: `Microsoft.Orleans.Clustering.AzureStorage`
- ADO.NET databases: `Microsoft.Orleans.Clustering.AdoNet`
- Amazon DynamoDB: `Microsoft.Orleans.Clustering.DynamoDB`
- Redis: `Microsoft.Orleans.Clustering.Redis`
- Apache ZooKeeper: `Microsoft.Orleans.Clustering.ZooKeeper`
- Consul: [`Microsoft.Orleans.Clustering.Consul`](../host/configuration-guide/clustering/consul.md)

See [Typical configurations](../host/configuration-guide/typical-configurations.md) and the [Orleans packages list](../resources/nuget-packages.md) for configuration and package links. Orleans doesn't require the Kubernetes hosting package. If used for a simple one-`Deployment`-per-cluster topology, it supplements a clustering provider; it doesn't replace one.

Evaluate:

- Availability and consistency guarantees in every failure mode.
- Expected membership read/write rate during rollout or recovery.
- Authentication, transport encryption, network isolation, and least privilege.
- Retention and cleanup of old membership rows.
- Operational ownership, backup requirements, and regional recovery behavior.

## Network policy

Allow only the required paths:

- Silo port: trusted silos in the same cluster.
- Gateway port: trusted Orleans clients.
- Application ingress: the application's HTTP, gRPC, or other public protocol.
- Provider endpoints: the identities and destinations required by each configured provider.

Don't expose the silo port or gateway port to the public internet. If clients cross an untrusted network, use [Orleans TLS](../host/transport-layer-security.md) and enforce workload identity at the surrounding network boundary.

## Validate connectivity

Before sending traffic:

1. Record the advertised silo and gateway endpoint for each instance.
1. Test TCP connectivity from another silo to every advertised silo endpoint.
1. Test TCP connectivity from a client network to gateway endpoints.
1. Confirm cluster membership contains only the intended service ID, cluster ID, and live instances.
1. Repeat the checks during a rolling replacement and after a host restart.

A platform service or load balancer can be useful for HTTP ingress, but Orleans membership must still advertise endpoints that route to each individual silo.
