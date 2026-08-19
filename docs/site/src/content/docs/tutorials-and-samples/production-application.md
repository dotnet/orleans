---
title: Deploy an Orleans application to Azure Container Apps
description: Run, inspect, deploy, and verify a multi-process Orleans application on Azure Container Apps.
ms.date: 08/19/2026
ms.topic: tutorial
---

# Deploy an Orleans application to Azure Container Apps

This walkthrough takes you from an empty directory to a deployed, observable Orleans cluster using the [Azure Container Apps sample](https://github.com/dotnet/orleans/tree/main/samples/Deployment/AzureContainerApps). The application, infrastructure, and deployment workflow are versioned and validated together.

The finished system has dedicated silos, an HTTP API and worker client, Azure Table Storage clustering, managed identity, health probes, Application Insights, and an Orleans Dashboard. This sample demonstrates clustering and deployment; add a grain-storage provider to persist application state.

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
1. Stop the worker and confirm that API calls continue; the worker and Minimal API are separate Orleans clients, so restarting the worker leaves the API client connected to the silo cluster.

The sample dashboard uses open access for local development. Keep that endpoint on a trusted local host. For a deployed environment, [secure the dashboard](../dashboard/index.md#secure-the-dashboard-before-exposing-it) with HTTPS, operator authentication and authorization, and a private administrative path.

## Understand the production configuration

Development uses a storage emulator. Deployed processes use <xref:Azure.Identity.DefaultAzureCredential> with a user-assigned managed identity and an Azure Table service URI. Token-based data-plane access supplies storage authorization.

Every deployed silo has a unique advertised silo and gateway endpoint. Azure Container Apps assigns the documented endpoint at the app boundary, so the sample deploys each silo as a separate one-replica Container App. Add capacity by adding silo apps with unused ports.

Review the sample's [deployment README](https://github.com/dotnet/orleans/blob/main/samples/Deployment/AzureContainerApps/README.md) and `Azure/bootstrap.bicep` before assigning roles. The privileged bootstrap and routine deployment are deliberately separate.

### Configure the host in code

Orleans production configuration is composed on the .NET Generic Host. The [sample silo host](https://github.com/dotnet/orleans/blob/main/samples/Deployment/AzureContainerApps/Silo/Program.cs) reads deployment values through <xref:Microsoft.Extensions.Configuration.IConfiguration>, calls <xref:Microsoft.Extensions.Hosting.OrleansSiloGenericHostExtensions.UseOrleans*>, and configures cluster identity, endpoints, and Azure Table Storage clustering on the resulting <xref:Orleans.Hosting.ISiloBuilder>.

The same pattern can register durable grain storage in an application which persists grain state:

:::code language="csharp" source="../snippets/compiled/Deployment/DeploymentSnippets.cs" id="container_apps_storage_usings":::
:::code language="csharp" source="../snippets/compiled/Deployment/DeploymentSnippets.cs" id="configure_container_apps_storage":::

`ServiceId` remains stable for the application. `ClusterId` identifies the deployment environment or blue-green cluster. Every silo and external client uses the same values and the same clustering backend. The host reads provider endpoints and cluster identity from deployment configuration and fails startup when required values are absent.

Listening endpoints describe where the process accepts connections. Advertised endpoints identify the unique address and ports which other silos and clients use to reach that process:

:::code language="csharp" source="../snippets/compiled/Deployment/DeploymentSnippets.cs" id="container_endpoint_usings":::
:::code language="csharp" source="../snippets/compiled/Deployment/DeploymentSnippets.cs" id="configure_container_endpoints":::

The deployment platform supplies these values for each silo. The sample's [endpoint configuration](https://github.com/dotnet/orleans/blob/main/samples/Deployment/AzureContainerApps/Infrastructure/OrleansEndpointConfigurationExtensions.cs) applies the same model to the private address and unique port pair allocated to each one-replica Container App.

### Choose a deployment model

Use the platform guide whose network and lifecycle guarantees match the target environment:

| Target | Recommended model |
| --- | --- |
| Kubernetes | Advertise each pod IP, allow direct pod-to-pod TCP, and use a production clustering provider. |
| Managed container platform | Give each silo a documented per-instance address or a unique private address and port pair. |
| Virtual machines or bare metal | Advertise stable private addresses and supervise the .NET host as a long-running service. |
| Azure App Service | Use the multi-instance sample and its private per-instance port mapping. |

See [Platform requirements](../deployment/platform-guides.md) before adapting the sample to another host. The invariant is that every membership entry names one silo endpoint which all other silos can reach directly.

## Deploy

1. Fork the Orleans repository and enable GitHub Actions.
1. Configure a GitHub OIDC identity restricted to your fork and a protected `azure-container-apps` environment.
1. Run the sample's one-time privileged bootstrap to create the registry, membership storage, runtime identity, and least-privilege role assignments.
1. Copy `samples/Deployment/AzureContainerApps/deployment/deploy.yml` to `.github/workflows/deploy-orleans-container-apps.yml`.
1. Configure the dashboard host and ingress for the operator controls described above.
1. Add the environment variables listed in the sample README, then run the workflow.

The workflow builds images, pushes immutable Git-SHA tags, deploys by image digest, and authenticates through GitHub OIDC and workload identity.

## Verify the deployment

Connect through the operator-only administrative path, then:

1. Confirm that every expected silo is active in the dashboard.
1. Exercise `GET /hello/0`, `GET /hello/255`, and `GET /providers`.
1. Verify that invalid grain keys return HTTP 400.
1. Confirm that startup, readiness, and liveness probes are healthy.
1. Inspect traces and logs in Application Insights and confirm that requests cross the API-to-grain boundary.

Before adapting this system for production, work through the [production-readiness checklist](../deployment/production-readiness.md), configure [durable grain storage](../grains/grain-persistence/index.md), and plan [graceful shutdown and upgrades](../deployment/upgrades.md).
