---
title: Orleans how-to guides
description: Task-oriented recipes for configuring, deploying, and operating Orleans applications.
ms.date: 08/11/2026
ms.topic: how-to
---

# Orleans how-to guides

Use these recipes when you have a specific task to complete. Each guide focuses on prerequisites, the shortest supported path, and the checks which confirm that the change worked.

If you are learning Orleans from an empty directory, start with the [tutorials and walkthroughs](../tutorials-and-samples/index.md). For the ideas behind these tasks, see [Orleans concepts](../overview.md) and [Why Orleans?](../benefits.md). For the runtime behavior behind a recommendation, see [Architecture and internals](../implementation/index.md).

## Configure hosting and providers

- [Configure a silo](../host/configuration-guide/server-configuration.md)
- [Configure an external client](../host/configuration-guide/client-configuration.md)
- [Choose a typical configuration](../host/configuration-guide/typical-configurations.md)
- [Configure ADO.NET providers](../host/configuration-guide/configuring-ado-dot-net-providers.md)
- [Configure Consul clustering](../host/configuration-guide/clustering/consul.md)
- [Configure serialization](../host/configuration-guide/serialization-configuration.md)
- [Configure TLS](../host/transport-layer-security.md)
- [Configure Aspire integration](../host/aspire-integration.md)

## Deploy and operate

- [Review production readiness](../deployment/production-readiness.md)
- [Configure topology, networking, and clustering](../deployment/networking.md)
- [Deploy to Kubernetes](../deployment/kubernetes.md)
- [Deploy to Azure App Service](../deployment/deploy-to-azure-app-service.md)
- [Deploy to Azure Container Apps](../deployment/deploy-to-azure-container-apps.md)
- [Plan capacity and scaling](../deployment/capacity-planning.md)
- [Perform a graceful upgrade](../deployment/upgrades.md)
- [Plan disaster recovery](../deployment/disaster-recovery.md)

## Use Orleans features

- [Persist grain state](../grains/grain-persistence/index.md)
- [Add reminders and timers](../grains/timers-and-reminders.md)
- [Configure Amazon DynamoDB reminders](../grains/reminders/dynamodb.md)
- [Configure streams](../streaming/stream-providers.md)
- [Use response streaming](../grains/response-streaming.md)
- [Use stateless worker grains](../grains/stateless-worker-grains.md)
- [Test an Orleans application](../grains/testing.md)

## Diagnose and recover

- [Troubleshoot a deployment](../deployment/troubleshooting-deployments.md)
- [Troubleshoot Orleans incidents](../host/monitoring/troubleshooting.md)
- [Monitor silo and client error codes](../host/monitoring/silo-error-code-monitoring.md)
- [Configure signals and alerting](../host/monitoring/signals.md)
- [Handle failures and uncertain outcomes](../deployment/handling-failures.md)

For public types, defaults, exceptions, and overloads, use the [C# API reference](https://dotnet.github.io/orleans/docs/api/csharp/). Reference entries provide the precise contract; the recipes above show how to apply it.
