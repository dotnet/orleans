---
title: Test Orleans applications
description: Test Orleans applications with InProcessTestCluster, shared fixtures, dynamic silos, and isolated unit tests.
ms.date: 08/08/2026
ms.topic: how-to
---

# Test Orleans applications

Orleans applications benefit from tests at more than one level:

| Test level | Best suited for |
| --- | --- |
| Isolated unit test | Pure application logic and constructor-injected collaborators |
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

## Use mocks for isolated logic

Test pure domain behavior without a cluster when the code does not depend on the Orleans runtime. Prefer constructor injection or extract the behavior into an ordinary service which is easy to instantiate. A mocked <xref:Orleans.IGrainFactory> or grain reference can verify collaboration, but it does not validate:

- grain activation or deactivation;
- request interleaving and reentrancy;
- serialization and deep copying;
- placement, directory lookup, or message routing; or
- provider and lifecycle behavior.

Do not widen production APIs solely so a mocking framework can override inherited runtime members. When any runtime behavior above is part of the assertion, use a test cluster instead.
