---
title: Test an Orleans application end to end
description: Progress from a first grain test to reusable cluster and topology tests with Orleans.TestingHost.
ms.date: 08/11/2026
ms.topic: tutorial
---

# Test an Orleans application end to end

This walkthrough builds a test suite in layers: pure logic tests, a real in-process Orleans cluster, a shared cluster fixture, and a topology change. The complete, buildable source is in the documentation's `grains/snippets/testing/orleans-testing` project.

## Choose the right boundary

Use ordinary unit tests for pure application logic, <xref:Orleans.TestingHost.InProcessTestCluster> for Orleans runtime behavior, and production-provider tests for external contracts.

These boundaries preserve fast feedback and runtime fidelity.

## Run the first cluster test

Clone the repository, then run the maintained test project:

```powershell
git clone https://github.com/dotnet/orleans.git
cd orleans
dotnet test .\docs\site\src\content\docs\grains\snippets\testing\orleans-testing\Sample.OrleansTesting\Sample.OrleansTesting.csproj
```

The first test creates a cluster, deploys it, obtains a grain reference from the client, makes a call, and disposes the cluster:

:::code language="csharp" source="../grains/snippets/testing/orleans-testing/Sample.OrleansTesting/HelloGrainTests.cs" id="basic_cluster_test":::

This validates code generation, serialization, activation, message dispatch, and the grain implementation together. The default builder starts two silos and a client with fast in-memory test infrastructure.

## Add application configuration

Production grains commonly depend on services registered through dependency injection. Configure all hosts, only silos, or only the client using the matching builder scope:

:::code language="csharp" source="../grains/snippets/testing/orleans-testing/Sample.OrleansTesting/ClusterConfiguration.cs" id="configure_cluster":::

Each host owns a service provider. Registering a service type creates one instance per host. Registering a captured instance intentionally shares it across silos and the client, so shared test doubles must be thread-safe.

Run the tests again after each configuration change. Propagate deployment failures through test setup so the test run reports them as failures.

## Reuse an expensive cluster

Create an xUnit fixture when multiple tests need identical cluster configuration:

:::code language="csharp" source="../grains/snippets/testing/orleans-testing/Sample.OrleansTesting/ClusterFixture.cs" id="cluster_fixture":::

Register the fixture as a collection:

:::code language="csharp" source="../grains/snippets/testing/orleans-testing/Sample.OrleansTesting/ClusterCollection.cs" id="cluster_collection":::

Then consume the fixture's already-started cluster from every test:

:::code language="csharp" source="../grains/snippets/testing/orleans-testing/Sample.OrleansTesting/HelloGrainTestsWithFixture.cs" id="shared_cluster_test":::

Give each test unique grain identities, reset shared external state, and make every test order-independent.

## Exercise a topology change

Add a silo, wait for membership to stabilize, then stop it gracefully:

:::code language="csharp" source="../grains/snippets/testing/orleans-testing/Sample.OrleansTesting/ClusterConfiguration.cs" id="change_topology":::

Use this pattern to test behavior which depends on membership changes. A same-process cluster exercises membership changes. Use a separate environment to exercise process crashes, network partitions, socket transport, and production membership providers.

## Add production-provider tests

For persistence, reminders, clustering, or streams, add an opt-in suite which:

1. Provisions an isolated provider instance or emulator.
1. Configures the cluster with the same provider extension used in production.
1. Uses unique database, table, stream, and grain identifiers.
1. Verifies behavior across a silo restart.
1. Cleans up owned resources even when an assertion fails.

Store credentials in the test environment's secret facility and report missing prerequisites explicitly. For API details and fidelity boundaries, see [Test Orleans applications](../grains/testing.md) and [TestingHost architecture](../implementation/testing.md).
