---
title: Test an Orleans application end to end
description: Progress from a first grain test to reusable cluster and topology tests with Orleans.TestingHost.
ms.date: 08/11/2026
ms.topic: tutorial
---

# Test an Orleans application end to end

This walkthrough builds a test suite in layers: pure logic tests, a real in-process Orleans cluster, a shared cluster fixture, and a topology change. The complete compiling source is in the documentation's `snippets/testing/orleans-testing` project.

## Choose the right boundary

Use ordinary unit tests for code which doesn't depend on activation, scheduling, serialization, placement, or providers. Use <xref:Orleans.TestingHost.InProcessTestCluster> when any of those Orleans behaviors matters. Finally, test against production providers when their external-system contract is part of the behavior.

Keeping these boundaries explicit makes the fast tests fast without creating false confidence from mocked runtime behavior.

## Run the first cluster test

Clone the repository, then run the maintained test project:

```powershell
git clone https://github.com/dotnet/orleans.git
cd orleans
dotnet test .\docs\site\src\content\docs\snippets\testing\orleans-testing\Sample.OrleansTesting
```

The first test creates a cluster, deploys it, obtains a grain reference from the client, makes a call, and disposes the cluster:

:::code language="csharp" source="../grains/snippets/testing/orleans-testing/Sample.OrleansTesting/HelloGrainTests.cs" id="basic_cluster_test":::

This validates code generation, serialization, activation, message dispatch, and the grain implementation together. The default builder starts two silos and a client with fast in-memory test infrastructure.

## Add application configuration

Production grains commonly depend on services registered through dependency injection. Configure all hosts, only silos, or only the client using the matching builder scope:

:::code language="csharp" source="../grains/snippets/testing/orleans-testing/Sample.OrleansTesting/ClusterConfiguration.cs" id="configure_cluster":::

Each host owns a service provider. Registering a service type creates one instance per host. Registering a captured instance intentionally shares it across silos and the client, so shared test doubles must be thread-safe.

Run the tests again after each configuration change. A deployment failure should fail the test setup rather than be converted into a passing assertion.

## Reuse an expensive cluster

Create an xUnit fixture when multiple tests need identical cluster configuration:

:::code language="csharp" source="../grains/snippets/testing/orleans-testing/Sample.OrleansTesting/ClusterFixture.cs" id="cluster_fixture":::

Register the fixture as a collection:

:::code language="csharp" source="../grains/snippets/testing/orleans-testing/Sample.OrleansTesting/ClusterCollection.cs" id="cluster_collection":::

Then consume the cluster without starting it in every test:

:::code language="csharp" source="../grains/snippets/testing/orleans-testing/Sample.OrleansTesting/HelloGrainTestsWithFixture.cs" id="shared_cluster_test":::

Give each test unique grain identities, reset shared external state, and don't depend on test execution order.

## Exercise a topology change

Add a silo, wait for membership to stabilize, then stop it gracefully:

:::code language="csharp" source="../grains/snippets/testing/orleans-testing/Sample.OrleansTesting/ClusterConfiguration.cs" id="change_topology":::

Use this pattern to test behavior which depends on membership changes. A same-process cluster doesn't reproduce process crashes, network partitions, socket transport, or a production membership provider. Add a separate environment for those failure modes instead of treating this test as equivalent.

## Add production-provider tests

For persistence, reminders, clustering, or streams, add an opt-in suite which:

1. Provisions an isolated provider instance or emulator.
1. Configures the cluster with the same provider extension used in production.
1. Uses unique database, table, stream, and grain identifiers.
1. Verifies behavior across a silo restart.
1. Cleans up owned resources even when an assertion fails.

Keep credentials out of source control and make missing prerequisites explicit. For API details and fidelity boundaries, see [Test Orleans applications](../grains/testing.md) and [TestingHost architecture](../implementation/testing.md).
