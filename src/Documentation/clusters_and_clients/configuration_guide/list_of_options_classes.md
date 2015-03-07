---
layout: page
title: List of Options Classes
---

# List of Options Classes

All Options classes used to configure Orleans should be in the `Orleans.Configuration` namespace. Many of them have helper methods in the `Orleans.Hosting` namespace.

## Common core options for IClientBuilder and ISiloHostBuilder

| Option type | Used for |
|-------------|----------|
| `ClusterOptions` | Setting the `ClusterId` and the `ServiceId` |
| `NetworkingOptions` | Setting timeout values for sockets and opened connections |
| `SerializationProviderOptions` | Setting the serialization providers |
| `TypeManagementOptions` | Setting the refresh period of the Type Map (see Heterogeneous silos and Versioning) |

## IClientBuilder specific options

| Option type | Used for |
|-------------|----------|
| `ClientMessagingOptions` | Setting the number of connection to keep open, and specify what network interface to use |
| `ClientStatisticsOption` | Setting various setting related to statistics output |
| `GatewayOptions` | Setting the refresh period of the list of available gateways |

## ISiloHostBuilder specific options

| Option type | Used for |
|-------------|----------|
| `ClusterMembershipOptions` | Settings for cluster membership |
| `ConsistentRingOptions` | Configuration options for consistent hashing algorithm, used to balance resource allocations across the cluster. |
| `EndpointOptions` | Setting the Silo endpoint options |
| `GrainCollectionOptions` | Options for grain garbage collection |
| `GrainVersioningOptions` |  Governs grain implementation selection in heterogeneous deployments |
| `LoadSheddingOptions` | Settings for load shedding configuration |
| `MultiClusterOptions` | Options for configuring multi-cluster support |
| `PerformanceTuningOptions` | Performance tuning options (networking, number of threads) |
| `ProcessExitHandlingOptions` | Configure silo behavior on process exit |
| `SchedulingOptions` | Configuring scheduler behavior |
| `SiloMessagingOptions` | Configuring global messaging options that are silo related. |
| `SiloOptions` | Setting the name of the Silo |
| `SiloStatisticsOptions` |  Setting various setting related to statistics output |
| `TelemetryOptions` | Setting telemetry consumer settings |


















