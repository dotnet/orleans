---
title: Build and deploy a production-shaped Orleans application
description: Run, inspect, deploy, and verify a multi-process Orleans application on Azure Container Apps.
ms.date: 08/11/2026
ms.topic: tutorial
---

# Build and deploy a production-shaped Orleans application

This walkthrough takes you from an empty directory to a deployed, observable Orleans cluster. You use the maintained [Azure Container Apps sample](https://github.com/dotnet/orleans/tree/main/samples/Deployment/AzureContainerApps) so that the application, infrastructure, and deployment workflow stay buildable together.

The finished system has dedicated silos, an HTTP API and worker client, Azure Table Storage clustering, managed identity, health probes, Application Insights, and an Orleans Dashboard. Grain state isn't persisted in this sample; add a grain-storage provider before storing application state.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Git](https://git-scm.com/downloads)
- [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) for local clustering
- An Azure subscription and [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) for deployment

## Get the application

From an empty working directory:

```powershell
git clone https://github.com/dotnet/orleans.git
cd orleans
dotnet build .\samples\Deployment\AzureContainerApps\HelloOrleans.sln -c Release
```

Explore the projects before running them:

| Project | Responsibility |
| --- | --- |
| `Abstractions` | Grain interfaces and serialized contracts shared by callers and silos. |
| `Grains` | Grain implementations and placement policy. |
| `Silo` | A dedicated Orleans server process. |
| `Clients.MinimalApi` | An HTTP API which calls grains through an Orleans client. |
| `Clients.WorkerService` | A background client which sends simulated sensor updates. |
| `Dashboard` | A separately deployed dashboard silo. |
| `Infrastructure` | Shared identity, clustering, endpoint, and telemetry configuration. |

This separation lets clients and silos scale and deploy independently. It also keeps contracts independent from implementations.

## Run the system locally

Start Azurite, then open four terminals at the repository root:

```powershell
dotnet run --project .\samples\Deployment\AzureContainerApps\Silo
dotnet run --project .\samples\Deployment\AzureContainerApps\Dashboard
dotnet run --project .\samples\Deployment\AzureContainerApps\Clients.MinimalApi
dotnet run --project .\samples\Deployment\AzureContainerApps\Clients.WorkerService
```

Each launch profile selects the Development environment and connects to Azurite. Verify the system before deploying:

1. Open the dashboard URL printed by the `Dashboard` process and confirm that its silo and the dedicated silo are active.
1. Call `GET /hello/0` on the Minimal API and confirm that it returns a greeting.
1. Call `GET /providers` and confirm that grain key `0` appears.
1. Stop the worker and confirm that API calls continue; clients aren't cluster members and can restart independently.

## Understand the production configuration

Development uses a storage emulator. Deployed processes instead use <xref:Azure.Identity.DefaultAzureCredential> with a user-assigned managed identity and an Azure Table service URI. The deployment grants data-plane access without distributing storage keys.

Every deployed silo has a unique advertised silo and gateway endpoint. Azure Container Apps replicas within one app don't provide Orleans with unique, documented replica addresses, so the sample deploys each silo as a separate one-replica Container App. Add capacity by adding another silo app with unused ports, not by increasing a silo app's replica count.

Review the sample's [deployment README](https://github.com/dotnet/orleans/blob/main/samples/Deployment/AzureContainerApps/README.md) and `Azure/bootstrap.bicep` before assigning roles. The privileged bootstrap and routine deployment are deliberately separate.

## Deploy

1. Fork the Orleans repository and enable GitHub Actions.
1. Configure a GitHub OIDC identity restricted to your fork and a protected `azure-container-apps` environment.
1. Run the sample's one-time privileged bootstrap to create the registry, membership storage, runtime identity, and least-privilege role assignments.
1. Copy `samples/Deployment/AzureContainerApps/deployment/deploy.yml` to `.github/workflows/deploy-orleans-container-apps.yml`.
1. Add the environment variables listed in the sample README, then run the workflow.

The workflow builds images, pushes immutable Git-SHA tags, deploys by image digest, and authenticates without a client secret.

## Verify the deployment

Connect from the environment's virtual network, then:

1. Confirm that every expected silo is active in the dashboard.
1. Exercise `GET /hello/0`, `GET /hello/255`, and `GET /providers`.
1. Verify that invalid grain keys return HTTP 400.
1. Confirm that startup, readiness, and liveness probes are healthy.
1. Inspect traces and logs in Application Insights and confirm that requests cross the API-to-grain boundary.

Before adapting this system for production, work through the [production-readiness checklist](../deployment/production-readiness.md), configure [durable grain storage](../grains/grain-persistence/index.md), and plan [graceful shutdown and upgrades](../deployment/upgrades.md).
