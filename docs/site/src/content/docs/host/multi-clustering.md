---
title: Orleans multi-clustering
description: Federate Orleans clusters using universal references, cluster locators, and inter-cluster transports.
ms.date: 08/31/2026
ms.topic: concept-article
---

# Orleans multi-clustering

Orleans multi-clustering federates autonomous clusters which share a `ServiceId`. Each cluster retains its own membership, grain directory, placement, client directory, and failure detection. Federation resolves the destination cluster before the existing local routing system resolves the destination activation, observer, client, or system target.

## Universal references

<xref:Orleans.Runtime.UniversalReference> carries the grain identity, interface projection, service identity, and binding.

- A **virtual** reference uses an <xref:Orleans.Runtime.IClusterLocator> to select its destination cluster.
- A **cluster-bound** reference carries its destination `ClusterId`.

Cluster-local grains, observers, clients, and system targets use cluster-bound references when metacluster semantics are enabled. System-target grain IDs continue to identify the exact silo incarnation inside the selected cluster.

Reference serialization preserves the binding when references pass through grain calls, storage, or external systems using the Orleans serializer. Reference equality includes the service and binding. Casting a reference to another interface preserves its universal identity.

## Cluster location and placement

Apply <xref:Orleans.Placement.ClusterLocatorAttribute> to a grain implementation to select a named cluster locator. Grain types without this attribute remain cluster-bound.

A cluster locator returns the authoritative cluster location for a virtual grain. Implementations can compute the result from the grain ID, external tenant metadata, or a mutable directory.

<xref:Orleans.Runtime.Placement.RendezvousClusterLocator> deterministically maps grains across active clusters in the current topology. It stores no per-grain mapping.

<xref:Orleans.Runtime.Placement.DirectoryClusterLocator> stores mutable ownership through <xref:Orleans.Runtime.IClusterDirectory>. When no mapping exists, it consults the grain's cluster placement strategy and atomically records the selected owner.

<xref:Orleans.Runtime.ClusterPlacementStrategy> proposes candidate clusters for directory-backed first placement or relocation. The locator remains responsible for recording and resolving ownership.

## Topology

<xref:Orleans.Runtime.IMetaclusterTopologyProvider> supplies versioned cluster topology. A topology identifies:

- The shared service.
- Its epoch.
- Cluster administrative states.
- Relay endpoints.
- Cluster metadata.

The built-in static provider reads peers from <xref:Orleans.Configuration.MetaclusterOptions.Clusters>. Applications can register another provider for dynamic control-plane integration.

Administrative state and observed transport reachability are separate. A failed connection changes reachability; a topology epoch joins, drains, or removes a cluster.

## Inter-cluster transport

<xref:Orleans.Runtime.IInterClusterTransport> carries resolved requests to another cluster. The destination dispatches them through <xref:Orleans.Runtime.IInterClusterRequestReceiver>, which enters the existing local Orleans routing path.

<xref:Orleans.Runtime.ClientInterClusterTransport> is the built-in transport. It obtains a connected <xref:Orleans.IClusterClient> from <xref:Orleans.Runtime.IInterClusterClientProvider> and invokes the destination cluster's relay grain. Applications own remote client configuration, connection lifetime, credentials, and endpoint selection.

Federation ingress fails closed until the silo registers an <xref:Orleans.Runtime.IInterClusterRequestAuthorizer>. The authorizer validates the authenticated caller and claimed source cluster before topology, ownership, interface, or system-target checks proceed.

Requests preserve their Orleans invocation body, response, timeout, cancellation, and one-way behavior. Observer references passed to another cluster retain their home cluster, so callbacks return through that cluster's client directory.

System-target requests are accepted when their grain type is listed in <xref:Orleans.Configuration.MetaclusterOptions.ExportedSystemTargets>. The destination routes an accepted request to the silo encoded by its system-target grain ID.

## Directory ownership

Directory-backed mappings store:

- Owner `ClusterId`.
- Ownership version.
- Topology epoch.
- Lease expiration.
- Monotonic fencing token.

The destination validates and renews directory ownership before dispatching a federated request. Each directory-backed activation validates ownership before activation, renews its lease while active, and deactivates when renewal fails. Ownership transfers after the prior lease expires and assigns a higher version and fencing token. Versioned relocation uses compare-and-set after lease expiry so stale topology views cannot overwrite newer ownership.

<xref:Orleans.Runtime.IClusterOwnershipAccessor> exposes the current ownership record to storage and integration providers. Those providers can persist and validate the fencing token when an external resource requires stale-owner exclusion.

<xref:Orleans.Runtime.Placement.InMemoryClusterDirectory> supports development and testing. Production providers implement <xref:Orleans.Runtime.IClusterDirectory> using a durable service with atomic compare-and-set and consistent time semantics.

## Configuration

Call <xref:Orleans.Hosting.ClusterPlacementExtensions.UseMetacluster*> on clients and <xref:Orleans.Hosting.ClusterPlacementSiloBuilderExtensions.UseMetacluster*> on silos. Configure every participating host with the same `ServiceId` and a distinct `ClusterId`.

Register matching locator names and topology behavior in every cluster which can create or invoke a virtual reference. Register an inter-cluster transport on every cluster which sends federated requests or receives callbacks.

The relevant timing options are:

- <xref:Orleans.Configuration.MetaclusterOptions.ClusterLocationCacheDuration>
- <xref:Orleans.Configuration.MetaclusterOptions.ClusterOwnershipLeaseDuration>
- <xref:Orleans.Configuration.MetaclusterOptions.ClusterOwnershipLeaseRenewalWindow>

## Operational guarantees

- Local calls continue through the normal Orleans routing path.
- Cluster-bound references preserve their destination across serialization.
- Virtual references are resolved once by the originating runtime for each routing attempt.
- Topology validation prevents new placement into draining or removed clusters.
- Directory-backed ownership is validated at federation ingress.
- An unavailable topology, locator, directory, remote client, or relay completes the originating call with an explicit failure.
- Federation remains opt-in, so existing single-cluster applications retain their current reference and routing behavior.
