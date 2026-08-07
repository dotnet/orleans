---
title: Local development configuration
description: Configure an Orleans application for local development.
ms.date: 08/02/2026
ms.topic: how-to
---

# Local development configuration

For the shortest development loop, host grains and the calling code in one process and use localhost clustering:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="local_silo_and_client":::

`UseOrleans` registers a co-hosted `IClusterClient`, so controllers, endpoints, hosted services, and other dependency-injected components can call grains without a separate client process.

`UseLocalhostClustering` configures loopback networking and development clustering. Memory storage and memory reminders are also development-only: their data is lost when the process stops.

## Run a separate local client

Use a separate process when the production architecture requires client and silo isolation:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="local_external_client":::

The client and silo must use matching gateway ports, `ServiceId`, and `ClusterId`. `UseLocalhostClustering` supplies matching defaults when both run on the same machine.

## Run multiple local silos

`UseLocalhostClustering` is optimized for a single local silo. To exercise membership changes or failover, use one of these approaches:

- Use [Aspire](../aspire-integration.md) with a containerized Redis or other supported clustering resource and multiple silo replicas.
- Configure a shared development clustering primary and assign each silo unique silo and gateway ports.
- Run the same production clustering provider against a local container or emulator.

Aspire is usually the easiest option because it allocates endpoints, starts dependencies, injects configuration, and displays logs for every replica.

> [!IMPORTANT]
> Don't deploy localhost, static, development, memory, or emulator-backed providers as production infrastructure. They don't provide the durability and availability expected from a multi-node deployment.

## Choose local backing services

Use in-memory providers for fast unit-level iteration. Use containers or emulators when you need to test provider behavior, serialization formats, schema setup, or restart durability. Keep the same provider package and configuration shape that production uses whenever practical.

For integration tests that create in-process clusters, use the [`Microsoft.Orleans.TestingHost`](https://www.nuget.org/packages/Microsoft.Orleans.TestingHost) package and `TestClusterBuilder` instead of manually assigning ports. For complete applications, see the [Orleans samples](https://github.com/dotnet/orleans/tree/main/samples).
