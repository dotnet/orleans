---
layout: page
title: Multi-Cluster Support
---

[!include[](../../warning-banner.md)]

# Multi-Cluster Support

Orleans v.1.3.0 added support for federating several Orleans clusters into a loosely connected *multi-cluster* that acts like a single service. 

Multi-clusters facilitate *geo-distribution* of a service, that is, make it easier to run an Orleans application in multiple data-centers around the world. Also, a multi-cluster can be run within a single datacenter to get better failure and performance isolation. 

All mechanisms are designed with particular attention to (1) minimize communication between clusters, and (2) let each cluster run autonomously even if other clusters fail or become unreachable. 

## Configuration and Operation

Below we document how to configure and operate a multi-cluster. 

[**Communication**](GossipChannels.md). Clusters communicate via the same silo-to-silo connections that are used within a cluster. To exchange status and configuration information, Clusters use a gossip mechanism and gossip channel implementations.

[**Silo Configuration**](SiloConfiguration.md). Silos need to be configured so they know which cluster they belong to (each cluster is identified by a unique string). Also, each silo needs to be configured with connection strings that allow them to connect to one or more [gossip channels](GossipChannels.md) on startup. 

[**Multi-Cluster Configuration Injection**](MultiClusterConfiguration.md). At runtime, the service operator can specify and/or change the *multi-cluster configuration*, which contains a list of cluster ids, to specify which clusters are part of the current multi-cluster. This is done by calling the management grain in any one of the clusters.

## Multi-Cluster Grains

Below we document how to use multi-cluster functionality at the application level.

[**Global-Single-Instance Grains**](GlobalSingleInstance.md). Developers can indicate when and how clusters should coordinate their grain directories with respect to a particular grain class. The  ``[GlobalSingleInstance]`` attribute means we want the same behavior as as when running Orleans in a single global cluster: that is, route all calls to a single activation of the grain. Conversely, the ``[OneInstancePerCluster]`` attribute indicates that each cluster can have its own independent activation. This is appropriate if communication between clusters is undesired.

**Log-View Grains**  _(not in v.1.3.0)_. A special type of grain that uses a new API, similar to event sourcing, for synchronizing or persisting grain state. It can be used to automatically and efficiently synchronize the state of  a grain between clusters and with storage. Because its synchronization algorithms are safe to use with reentrant grains, and are optimized to use batching and replication, it can perform better than standard grains when a grain is frequently accessed in multiple clusters, and/or when it is written to storage frequently. Support for log-view grains is not part of the master branch yet. We have a prerelease including samples and a bit of documentation in the [geo-orleans branch](https://github.com/sebastianburckhardt/orleans/tree/geo-samples). It is currently being evaluated in production by an early adopter. 

