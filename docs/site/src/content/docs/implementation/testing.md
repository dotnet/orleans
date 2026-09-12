---
title: TestingHost architecture
description: Understand Orleans in-process test clusters, substituted runtime services, builders, handles, and failure simulation.
ms.date: 08/02/2026
ms.topic: concept-article
---

# TestingHost architecture

The [`Microsoft.Orleans.TestingHost`](https://www.nuget.org/packages/Microsoft.Orleans.TestingHost/) package composes real silo and client hosts with test-oriented discovery, transport, statistics, directory, and lifecycle controls. It is an integration harness, not a mock grain runtime. Grain activation, scheduling, serialization, messaging, placement, and most provider behavior execute through the same runtime components as a hosted cluster.

This page describes the harness internals. For practical setup, fixture patterns, topology changes, and guidance on choosing mocks or a cluster, see [Test Orleans applications](../grains/testing.md).

## Cluster object model <a name="use-the-inprocesstestcluster-recommended"></a>

```mermaid
flowchart TB
    Builder[InProcessTestClusterBuilder]
    Options[InProcessTestClusterOptions]
    Cluster[InProcessTestCluster]
    Client[In-process ClusterClient]
    S1[InProcessSiloHandle 1]
    S2[InProcessSiloHandle 2]
    Hosts[Independent Generic Hosts]

    Builder --> Options
    Builder --> Cluster
    Cluster --> Client
    Cluster --> S1
    Cluster --> S2
    S1 --> Hosts
    S2 --> Hosts
```

<xref:Orleans.TestingHost.InProcessTestClusterBuilder> accumulates host, silo, and client delegates. <xref:Orleans.TestingHost.InProcessTestClusterBuilder.Build*> passes its options to an <xref:Orleans.TestingHost.InProcessTestCluster>; the cluster retains that mutable options object. <xref:Orleans.TestingHost.InProcessTestCluster.DeployAsync*> starts the configured silo hosts and initializes the client when requested. With client initialization enabled, it performs a best-effort initial membership-view check but can continue after warning that views have not stabilized. Each <xref:Orleans.TestingHost.InProcessSiloHandle> owns one Generic Host and exposes its service provider for test inspection.

The processes are shared, but the hosts and dependency-injection containers are not. Static process state can still leak between silos, which is one reason production code should not use static mutable state for silo-local behavior.

API: <xref:Orleans.TestingHost.InProcessTestClusterBuilder>, <xref:Orleans.TestingHost.InProcessTestCluster>, and <xref:Orleans.TestingHost.InProcessSiloHandle>. Implementation: [builder](https://github.com/dotnet/orleans/blob/main/src/Orleans.TestingHost/InProcTestClusterBuilder.cs), [cluster](https://github.com/dotnet/orleans/blob/main/src/Orleans.TestingHost/InProcTestCluster.cs), and [silo handle](https://github.com/dotnet/orleans/blob/main/src/Orleans.TestingHost/InProcessSiloHandle.cs).

## Defaults

<xref:Orleans.TestingHost.InProcessTestClusterBuilder> defaults to:

| Setting | Default |
| --- | --- |
| <xref:Orleans.TestingHost.InProcessTestClusterOptions.InitialSilosCount?displayProperty=nameWithType> | 2 |
| <xref:Orleans.TestingHost.InProcessTestClusterOptions.ClusterId?displayProperty=nameWithType> | Newly generated |
| <xref:Orleans.TestingHost.InProcessTestClusterOptions.ServiceId?displayProperty=nameWithType> | Newly generated GUID |
| <xref:Orleans.TestingHost.InProcessTestClusterOptions.InitializeClientOnDeploy?displayProperty=nameWithType> | `true` |
| Test-cluster membership | `true` |
| Test-cluster grain directory | `true` |
| <xref:Orleans.TestingHost.InProcessTestClusterOptions.UseDistributedGrainDirectory?displayProperty=nameWithType> | `false` |
| <xref:Orleans.TestingHost.InProcessTestClusterOptions.GatewayPerSilo?displayProperty=nameWithType> | `true` |
| <xref:Orleans.TestingHost.InProcessTestClusterOptions.UseRealEnvironmentStatistics?displayProperty=nameWithType> | `false` |
| Connection transport | In-memory |

Simulated environment statistics make resource-based tests deterministic, but they do not reproduce operating-system CPU or memory pressure. Opt into real statistics only when the test specifically needs those signals.

These defaults are defined by <xref:Orleans.TestingHost.InProcessTestClusterBuilder> and <xref:Orleans.TestingHost.InProcessTestClusterOptions>; see their [builder](https://github.com/dotnet/orleans/blob/main/src/Orleans.TestingHost/InProcTestClusterBuilder.cs) and [options](https://github.com/dotnet/orleans/blob/main/src/Orleans.TestingHost/InProcTestClusterOptions.cs) implementations.

## Configuration layers <a name="configure-the-test-cluster"></a>

The builder separates three scopes:

- <xref:Orleans.TestingHost.InProcessTestClusterBuilder.ConfigureHost*> applies to both silo and client Generic Hosts and is useful for shared configuration or keyed SDK clients.
- <xref:Orleans.TestingHost.InProcessTestClusterBuilder.ConfigureSilo*> receives per-silo options and an <xref:Orleans.Hosting.ISiloBuilder>.
- <xref:Orleans.TestingHost.InProcessTestClusterBuilder.ConfigureClient*> configures the cluster client.

Delegates run for each relevant host. A singleton registered by a silo delegate is singleton within that silo's container, not across the cluster. A concrete instance captured by a delegate is shared because the test supplied the same object.

## Dynamic topology <a name="add-and-remove-silos-during-tests"></a>

<xref:Orleans.TestingHost.InProcessTestCluster.StartAdditionalSiloAsync*> and <xref:Orleans.TestingHost.InProcessTestCluster.StartSilosAsync*> create new hosts using the cluster's current options and configuration delegates. <xref:Orleans.TestingHost.InProcessTestCluster.StopSiloAsync*>, <xref:Orleans.TestingHost.InProcessTestCluster.StopSilosAsync*>, <xref:Orleans.TestingHost.InProcessTestCluster.StopAllSilosAsync*>, <xref:Orleans.TestingHost.InProcessTestCluster.WaitForLivenessToStabilizeAsync*>, and <xref:Orleans.TestingHost.InProcessTestCluster.WaitForClusterManifestToStabilizeAsync*> coordinate membership and manifest transitions for failure and elasticity tests.

The overloads which take `startAdditionalSiloOnNewPort` are obsolete. Tests should use the parameterless <xref:Orleans.TestingHost.InProcessTestCluster.StartAdditionalSilo*> overloads or <xref:Orleans.TestingHost.InProcessTestCluster.StartSilosAsync*>. Lower-level <xref:Orleans.TestingHost.InProcessTestCluster.StartSiloAsync*> overloads remain available when a test must supply an instance number or configuration overrides.

Stopping a host gracefully exercises shutdown. Disposing or terminating a handle without graceful membership update is a different failure mode and should be chosen deliberately when testing failure detection.

## Class-configurator test cluster and custom silo creation <a name="use-the-testcluster"></a>

<xref:Orleans.TestingHost.TestClusterBuilder> is the class-configurator-based harness. It defaults to two silos, in-memory transport, generated cluster identity, test membership, client initialization, file logging, and homogeneous-silo assumptions. It also installs `ConfigureDistributedGrainDirectory`, configuring its silos with the distributed grain directory.

<xref:Orleans.TestingHost.ISiloConfigurator>, <xref:Orleans.TestingHost.IHostConfigurator>, and <xref:Orleans.TestingHost.IClientBuilderConfigurator> are serializable configuration identities which can be applied to every host. Assigning <xref:Orleans.TestingHost.TestClusterBuilder.CreateSiloAsync?displayProperty=nameWithType> sets <xref:Orleans.TestingHost.TestClusterOptions.ConnectionTransport?displayProperty=nameWithType> to `TcpSocket`, so the built-in client uses its TCP transport instead of the harness's in-memory transport. The delegate bypasses <xref:Orleans.TestingHost.TestCluster.DefaultCreateSiloAsync*> and cannot access the harness's private in-memory transport hub, so it must configure the custom silo host with a compatible transport.

<xref:Orleans.TestingHost.TestCluster> supports suites built around <xref:Orleans.TestingHost.SiloHandle> and configurator types. It does not use separate application domains; its hosts run in-process unless a custom silo creation path provides different isolation.

API: <xref:Orleans.TestingHost.TestClusterBuilder>, <xref:Orleans.TestingHost.TestCluster>, and <xref:Orleans.TestingHost.TestClusterOptions>. Implementation: [builder](https://github.com/dotnet/orleans/blob/main/src/Orleans.TestingHost/TestClusterBuilder.cs), [cluster](https://github.com/dotnet/orleans/blob/main/src/Orleans.TestingHost/TestCluster.cs), and [options](https://github.com/dotnet/orleans/blob/main/src/Orleans.TestingHost/TestClusterOptions.cs).

## Fidelity boundaries <a name="use-mocks"></a>

TestingHost deliberately substitutes infrastructure. Tests must account for those boundaries:

- in-memory transport does not reproduce sockets, TLS, packet loss, or network buffers;
- test membership does not prove a production membership provider's transaction behavior;
- the test grain directory can bypass production directory edge cases;
- simulated statistics do not reproduce load shedding;
- one process shares thread-pool and static state; and
- graceful stop does not model abrupt process or machine loss.

Use the smallest substitution which keeps the invariant under test real. Directory, membership, transport, and provider contract tests often need their production component explicitly enabled.

## Test architecture in the repository

Repository fixtures such as `BaseInProcessTestClusterFixture` wrap cluster setup and disposal, while specialized suites configure the component being exercised. For example, [`AQStreamingTests`](https://github.com/dotnet/orleans/blob/main/test/Extensions/Orleans.Azure.Tests/Streaming/AQStreamingTests.cs) uses `ConfigureHost`, `ConfigureSilo`, and `ConfigureClient` to combine real Azure Queue adapters with an in-process Orleans cluster.
