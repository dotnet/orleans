---
title: Test Orleans applications
description: Test Orleans applications with InProcessTestCluster, shared fixtures, dynamic silos, and isolated unit tests.
ms.date: 08/20/2026
ms.topic: how-to
---

# Test Orleans applications

Orleans applications benefit from tests at more than one level:

| Test level | Best suited for |
| --- | --- |
| Isolated unit test | Pure application logic and constructor-injected collaborators |
| <xref:Orleans.TestingHost.InProcessTestCluster> | Most grain tests, including grain calls, activation, scheduling, serialization, dependency injection, and cluster behavior |
| [OrleansTestKit](https://github.com/OrleansContrib/OrleansTestKit) | Basic arrange-act-assert tests of one grain activation whose correctness is independent of Orleans scheduling and concurrency |
| Test cluster with production providers | Storage, clustering, reminders, and streams whose external-system contract matters |

Default to <xref:Orleans.TestingHost.InProcessTestCluster> for grain code. It provides the highest-fidelity test boundary for Orleans runtime behavior. Test extracted calculations and application services directly. Use OrleansTestKit when the test author controls sequencing and synchronization.

## Create an in-process test cluster

Install the [`Microsoft.Orleans.TestingHost`](https://www.nuget.org/packages/Microsoft.Orleans.TestingHost/) package in the test project:

```dotnetcli
dotnet add package Microsoft.Orleans.TestingHost
```

For new Orleans tests, prefer <xref:Orleans.TestingHost.InProcessTestClusterBuilder>. Build the cluster, call <xref:Orleans.TestingHost.InProcessTestCluster.DeployAsync*>, use its client, and dispose the cluster after the test:

:::code language="csharp" source="snippets/testing/orleans-testing/Sample.OrleansTesting/HelloGrainTests.cs" id="basic_cluster_test":::

The default builder starts two silos and a client using in-memory transport, test membership, and a test grain directory. These substitutions make tests fast, but they do not reproduce socket transport or a production membership provider. See [TestingHost architecture](../implementation/testing.md) for the complete fidelity boundaries.

## Configure hosts, silos, and clients

The builder exposes separate configuration scopes:

- <xref:Orleans.TestingHost.InProcessTestClusterBuilder.ConfigureHost*> applies to every silo host and the client host.
- <xref:Orleans.TestingHost.InProcessTestClusterBuilder.ConfigureSilo*> applies to every silo.
- <xref:Orleans.TestingHost.InProcessTestClusterBuilder.ConfigureClient*> applies to the cluster client.

:::code language="csharp" source="snippets/testing/orleans-testing/Sample.OrleansTesting/ClusterConfiguration.cs" id="configure_cluster":::

Each host has its own dependency-injection container. Registering a type creates one singleton per container. Registering a captured instance, as in the example, deliberately shares that instance with the client and all silos. Shared mutable test doubles must therefore be thread-safe.

## Reuse a cluster across tests

Cluster startup is more expensive than an isolated unit test. Reuse a cluster across tests which need the same configuration by using the lifecycle support from the test framework.

### xUnit

Create an xUnit collection fixture:

:::code language="csharp" source="snippets/testing/orleans-testing/Sample.OrleansTesting/ClusterFixture.cs" id="cluster_fixture":::

Register the fixture as a collection and apply that collection to each test class which shares the cluster:

:::code language="csharp" source="snippets/testing/orleans-testing/Sample.OrleansTesting/ClusterCollection.cs" id="cluster_collection":::
:::code language="csharp" source="snippets/testing/orleans-testing/Sample.OrleansTesting/HelloGrainTestsWithFixture.cs" id="shared_cluster_test":::

### MSTest

Use MSTest assembly lifecycle methods to deploy one cluster for all MSTest classes in the test assembly:

:::code language="csharp" source="snippets/testing/orleans-testing/Sample.OrleansTesting/MSTestClusterFixture.cs" id="mstest_cluster_fixture":::

Tests access the deployed cluster through the fixture:

:::code language="csharp" source="snippets/testing/orleans-testing/Sample.OrleansTesting/MSTestClusterFixture.cs" id="mstest_shared_cluster_test":::

Shared-cluster tests must not depend on execution order. Give each test distinct grain identities and reset any shared external state. Use separate fixtures when suites require incompatible silo or provider configuration.

## Change cluster topology

An in-process cluster can add and remove silos while a test is running:

:::code language="csharp" source="snippets/testing/orleans-testing/Sample.OrleansTesting/ClusterConfiguration.cs" id="change_topology":::

Wait for liveness to stabilize before asserting behavior which depends on the new membership view. <xref:Orleans.TestingHost.InProcessTestCluster.StopSiloAsync*> performs a graceful stop. Abrupt process loss, network partitioning, and production transport behavior require a harness which introduces those failure modes explicitly.

## Maintain existing `TestCluster` suites

<xref:Orleans.TestingHost.TestCluster> remains available for suites built around class-based configurators such as <xref:Orleans.TestingHost.ISiloConfigurator>. Its built-in hosts run in process; a custom silo-creation delegate is required for another isolation model. New Orleans tests should generally use <xref:Orleans.TestingHost.InProcessTestCluster> for its delegate-based configuration and direct access to each host's service provider.

## Choose the runtime boundary

Use <xref:Orleans.TestingHost.InProcessTestCluster> when an assertion depends on runtime behavior. The cluster creates activations through the Orleans runtime and preserves turn scheduling, interleaving, serialization, dependency injection, persistence integration, placement, message routing, timers, reminders, and lifecycle behavior.

Pure domain logic can be extracted into an ordinary class or service and tested directly. Keep that boundary independent of Orleans runtime abstractions. A cluster test can replace application-owned collaborators through dependency injection while the runtime continues to create and execute the grain.

Configure the production storage or reminder provider when the test covers its external-system contract, concurrency behavior, or restart recovery.

## Use OrleansTestKit for a basic single-activation test

[OrleansTestKit](https://github.com/OrleansContrib/OrleansTestKit) is a community-maintained project in the OrleansContrib organization. It creates a fixture for one grain activation and supplies test implementations for activation identity, persistent state, grain references, timers, reminders, and streams. The test invokes grain code on its own execution context, so the test author controls sequencing and synchronization. This boundary suits basic arrange-act-assert tests of a single method whose result depends on injected values and recorded collaborator interactions. Match the OrleansTestKit major version to the Orleans major version used by the application.

Install the package in the test project:

```dotnetcli
dotnet add package OrleansTestKit
```

The following grain uses its string identity to address another grain and persists an item before making that call:

:::code language="csharp" source="snippets/testing/orleans-testing/Sample.OrleansTesting/ShoppingCartGrain.cs" id="testkit_grain":::

Derive the test class from `TestKitBase`. Register persistent state and grain probes on `Silo` before creating the grain with its test identity:

:::code language="csharp" source="snippets/testing/orleans-testing/Sample.OrleansTesting/ShoppingCartGrainTests.cs" id="testkit_grain_test":::

The test verifies the grain's state mutation, storage write request, key-derived grain reference, and outgoing call. OrleansTestKit invokes one grain using its simulated activation context and resolves collaborating grains as probes.

Use <xref:Orleans.TestingHost.InProcessTestCluster> for grains which await work, make concurrent calls, use reentrancy or interleaving, coordinate multiple activations, or depend on serialization, lifecycle, placement, timers, reminders, streams, or message routing. The runtime owns those behaviors, and the in-process cluster preserves their execution model.

The OrleansTestKit [README](https://github.com/OrleansContrib/OrleansTestKit/blob/main/README.md) documents version compatibility and package setup. Its [test suite](https://github.com/OrleansContrib/OrleansTestKit/tree/main/test/OrleansTestKit.Tests/Tests) demonstrates the fixture APIs and available test doubles for single-activation tests.
