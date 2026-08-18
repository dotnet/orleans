---
title: Test Orleans applications
description: Test Orleans applications with InProcessTestCluster, shared fixtures, dynamic silos, and isolated unit tests.
ms.date: 08/18/2026
ms.topic: how-to
---

# Test Orleans applications

Orleans applications benefit from tests at more than one level:

| Test level | Best suited for |
| --- | --- |
| Isolated unit test | Pure application logic and constructor-injected collaborators |
| [OrleansTestKit](https://github.com/OrleansContrib/OrleansTestKit) | A single grain's decisions and interactions using a simulated activation context |
| <xref:Orleans.TestingHost.InProcessTestCluster> | Grain calls, activation, scheduling, serialization, dependency injection, and cluster behavior |
| Test cluster with production providers | Storage, clustering, reminders, and streams whose external-system contract matters |

Use the smallest level which preserves the behavior under test. A mock can verify that code called a collaborator, but it cannot reproduce Orleans turn scheduling or message serialization. Conversely, starting a cluster for a pure calculation adds cost without increasing confidence.

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

## Reuse a cluster with an xUnit fixture

Cluster startup is more expensive than an isolated unit test. Reuse a cluster across tests which need the same configuration by using an xUnit collection fixture:

:::code language="csharp" source="snippets/testing/orleans-testing/Sample.OrleansTesting/ClusterFixture.cs" id="cluster_fixture":::

:::code language="csharp" source="snippets/testing/orleans-testing/Sample.OrleansTesting/ClusterCollection.cs" id="cluster_collection":::

:::code language="csharp" source="snippets/testing/orleans-testing/Sample.OrleansTesting/HelloGrainTestsWithFixture.cs" id="shared_cluster_test":::

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

## Test a grain in isolation with OrleansTestKit

[OrleansTestKit](https://github.com/OrleansContrib/OrleansTestKit) is a community-maintained project in the OrleansContrib organization. Its releases and issue tracking are managed in that repository. It creates a simulated grain activation context and supplies test implementations for activation identity, persistent state, grain references, timers, reminders, and streams. Match the OrleansTestKit major version to the Orleans major version used by the application.

Install the package in the test project:

```dotnetcli
dotnet add package OrleansTestKit
```

The following grain uses its string identity to address another grain and persists an item before making that call:

:::code language="csharp" source="snippets/testing/orleans-testing/Sample.OrleansTesting/ShoppingCartGrain.cs" id="testkit_grain":::

Derive the test class from `TestKitBase`. Register persistent state and grain probes on `Silo` before creating the grain with its test identity:

:::code language="csharp" source="snippets/testing/orleans-testing/Sample.OrleansTesting/ShoppingCartGrainTests.cs" id="testkit_grain_test":::

The test verifies the grain's state mutation, storage write request, key-derived grain reference, and outgoing call. OrleansTestKit invokes one grain using its simulated activation context and resolves collaborating grains as probes. Its timer and reminder helpers invoke registered callbacks directly. Use <xref:Orleans.TestingHost.InProcessTestCluster> for assertions governed by Orleans turn scheduling, interleaving, reentrancy, serialization, activation lifecycle, placement, timer or reminder scheduling, or message routing.

### More OrleansTestKit examples

The OrleansTestKit [README](https://github.com/OrleansContrib/OrleansTestKit/blob/main/README.md) documents version compatibility and package setup. The project's CI-run test suite provides additional recipes:

| Scenario | Upstream examples |
| --- | --- |
| Activation, deactivation, and lifecycle callbacks | [`BasicGrainTests`](https://github.com/OrleansContrib/OrleansTestKit/blob/main/test/OrleansTestKit.Tests/Tests/BasicGrainTests.cs) and [`ActivationGrainTests`](https://github.com/OrleansContrib/OrleansTestKit/blob/main/test/OrleansTestKit.Tests/Tests/ActivationGrainTests.cs) |
| Constructor and keyed service injection | [`DependencyGrainTests`](https://github.com/OrleansContrib/OrleansTestKit/blob/main/test/OrleansTestKit.Tests/Tests/DependencyGrainTests.cs) |
| Grain identities and activation context | [`GrainContextTests`](https://github.com/OrleansContrib/OrleansTestKit/blob/main/test/OrleansTestKit.Tests/Tests/GrainContextTests.cs) and [`GrainIdTests`](https://github.com/OrleansContrib/OrleansTestKit/blob/main/test/OrleansTestKit.Tests/Tests/GrainIdTests.cs) |
| `Grain<TState>` and persistent-state facets | [`StorageTests`](https://github.com/OrleansContrib/OrleansTestKit/blob/main/test/OrleansTestKit.Tests/Tests/StorageTests.cs) and [`StorageFacetTests`](https://github.com/OrleansContrib/OrleansTestKit/blob/main/test/OrleansTestKit.Tests/Tests/StorageFacetTests.cs) |
| Timers | [`TimerTests`](https://github.com/OrleansContrib/OrleansTestKit/blob/main/test/OrleansTestKit.Tests/Tests/TimerTests.cs) |
| Reminders | [`ReminderTests`](https://github.com/OrleansContrib/OrleansTestKit/blob/main/test/OrleansTestKit.Tests/Tests/ReminderTests.cs) |
| Streams, batches, and persistent subscriptions | [`StreamTests`](https://github.com/OrleansContrib/OrleansTestKit/blob/main/test/OrleansTestKit.Tests/Tests/StreamTests.cs), [`StreamBatchTests`](https://github.com/OrleansContrib/OrleansTestKit/blob/main/test/OrleansTestKit.Tests/Tests/StreamBatchTests.cs), and [`PersistentStreamWithinGrainStateTests`](https://github.com/OrleansContrib/OrleansTestKit/blob/main/test/OrleansTestKit.Tests/Tests/PersistentStreamWithinGrainStateTests.cs) |
| Strict grain and stream probes | [`StrictGrainProbeTests`](https://github.com/OrleansContrib/OrleansTestKit/blob/main/test/OrleansTestKit.Tests/Tests/StrictGrainProbeTests.cs) and [`StrictStreamTests`](https://github.com/OrleansContrib/OrleansTestKit/blob/main/test/OrleansTestKit.Tests/Tests/StrictStreamTests.cs) |

See the complete [OrleansTestKit test suite](https://github.com/OrleansContrib/OrleansTestKit/tree/main/test/OrleansTestKit.Tests/Tests) for probe factories, compound keys, class prefixes, stream IDs, storage failures, and custom service probes.
