---
title: Deploy Orleans to Azure App Service
description: Deploy and operate an Orleans cluster on Azure App Service using private instance endpoints and managed identity.
ms.date: 08/03/2026
ms.topic: tutorial
ms.custom: devops
---

# Deploy Orleans to Azure App Service

This tutorial deploys the [Orleans shopping cart sample](https://github.com/dotnet/orleans/tree/main/samples/Deployment/AzureAppService) as a multi-instance Orleans cluster on Azure App Service.

The sample cohosts an ASP.NET Core Blazor app and an Orleans silo in each App Service worker. It uses:

- A Premium v3 Windows App Service plan with three workers.
- Regional virtual network integration and two private TCP ports per instance.
- Azure Table Storage for Orleans membership and grain state.
- One dedicated user-assigned managed identity shared by both slots, with Microsoft Entra authorization.
- Separate production and staging slot clusters.
- App Service Health check, startup warm-up, and a bounded graceful shutdown.
- Application Insights and centralized Log Analytics.
- A GitHub Actions workflow authenticated using OpenID Connect (OIDC).

## Understand the App Service topology

Orleans silos communicate directly with individual silos over long-lived TCP connections. The App Service HTTP load balancer doesn't provide this connectivity.

Regional virtual network integration gives each App Service worker a private address in `WEBSITE_PRIVATE_IP`. When `vnetPrivatePortsCount` is `2`, App Service provides two ports in `WEBSITE_PRIVATE_PORTS` for inter-instance communication.

The sample:

1. Advertises `WEBSITE_PRIVATE_IP` and the allocated silo and gateway ports.
1. Listens on all local interfaces because the advertised private address might not be locally bindable.
1. Uses Azure Table Storage as the external Orleans clustering provider.
1. Integrates production and staging with separate delegated subnets.

Every silo must be able to reach every advertised silo address. Orleans clients outside the cohosted app must run in the same virtual network or a connected network that can reach each private gateway endpoint.

> [!IMPORTANT]
> Use a dedicated App Service compute tier that supports virtual network integration and deployment slots. Don't deploy this topology to Free, Shared, or an HTTP-only configuration.

The sample retains its existing two `/24` subnets. App Service consumes addresses during scale operations and platform upgrades, so size subnets for at least twice the planned maximum scale. For a new deployment, `/26` is a practical minimum for one plan with room to grow.

## Prerequisites

- An Azure subscription and permission to create resources and role assignments.
- The [.NET SDK selected by `global.json`](https://dotnet.microsoft.com/download).
- [Azure CLI](/cli/azure/install-azure-cli) with Bicep.
- A clone of the [`dotnet/orleans`](https://github.com/dotnet/orleans) repository.

Set local deployment values:

```powershell
$location = "westus3"
$resourceGroup = "orleans-shopping-cart"
$appName = "<globally-unique-lowercase-name-up-to-16-characters>"
```

## Run the app locally

From the repository root:

```powershell
dotnet run `
  --project .\samples\Deployment\AzureAppService\Silo\Orleans.ShoppingCart.Silo.csproj `
  --environment ASPNETCORE_ENVIRONMENT=Development
```

Development mode explicitly uses localhost clustering and in-memory state. Every nondevelopment environment requires the App Service private endpoint values and Azure Storage URI. Missing production configuration fails startup instead of silently creating a single-host cluster.

## Review Orleans endpoint configuration

The production host reads:

- `WEBSITE_PRIVATE_IP`
- `WEBSITE_PRIVATE_PORTS`
- `WEBSITE_INSTANCE_ID`
- `ORLEANS_SERVICE_ID`
- `ORLEANS_CLUSTER_ID`
- `ORLEANS_AZURE_STORAGE_URI`

It configures the assigned address and ports:

```csharp
siloBuilder.ConfigureEndpoints(
    privateIp,
    siloPort,
    gatewayPort,
    listenOnAnyHostAddress: true);
```

It then uses `DefaultAzureCredential` with a `TableServiceClient`. Both slots share one stable user-assigned identity, selected using `AZURE_CLIENT_ID`. The Bicep template assigns that identity **Storage Table Data Contributor**. The storage account disables shared-key authorization, so no storage account key or connection string is deployed.

Production and staging use the same stable `ServiceId` but different slot-sticky `ClusterId` values. Their membership and grain-state table names include the cluster ID, keeping the two slots isolated until a controlled swap warm-up.

## Deploy the infrastructure

Sign in and create a resource group:

```azurecli
az login
az group create --name $resourceGroup --location $location
```

Deploy the Bicep template:

```azurecli
az deployment group create `
  --resource-group $resourceGroup `
  --template-file .\samples\Deployment\AzureAppService\infra\flex\main.bicep `
  --parameters appName=$appName location=$location
```

The template creates:

- A three-worker Premium v3 Windows plan.
- Production and staging App Service slots with one shared user-assigned identity.
- A virtual network with separate `/24` delegated subnets.
- An Azure Storage account restricted to those subnets, with shared-key access disabled.
- Azure role assignments for table data.
- Log Analytics and workspace-based Application Insights.

The initial deployment is a privileged bootstrap operation because it creates a data-plane role assignment. Run it using an operator or provisioning identity with resource creation permission and permission to assign **Storage Table Data Contributor** in the resource group. Use [role-assignment conditions](/azure/role-based-access-control/delegate-role-assignments-portal) or a constrained custom role so the identity can assign only the required role to the application identity.

Routine application deployment doesn't need role-assignment permission. The included workflow passes `assignStorageRoles=false` and can use a deployment identity with **Contributor**, or a narrower custom deployment role, on the target resource group.

Role assignments can take several minutes to propagate. A first application start can fail until the managed identities can access table data.

### Upgrade an existing sample deployment

The template preserves the original Windows App Service plan and storage account names, the `ShoppingCartService` service ID, the `Default` and `Staging` cluster IDs, and existing membership and persistence table names.

If active instances still use a storage account key, don't run the new full template first. Its complete App Service configuration intentionally omits the legacy connection-string setting.

Migrate in this order:

1. Create the `${appName}-identity` user-assigned identity.
1. Assign it to the existing app and `${appName}stg` slot, then grant it **Storage Table Data Contributor** on `${appName}storage`.
1. Use `az webapp config appsettings set` to merge `AZURE_CLIENT_ID`, `ORLEANS_AZURE_STORAGE_URI`, `ORLEANS_SERVICE_ID=ShoppingCartService`, and the existing slot-specific cluster IDs into both slots. This preserves the legacy connection-string setting during transition.
1. Manually deploy the managed-identity build to `${appName}stg`, wait for readiness, and swap it into production.
1. Keep shared-key access enabled for the rollback window because the former production build now runs in staging.
1. After the rollback window, deploy and verify the managed-identity build in staging again.
1. Run the full template with `allowSharedKeyAccess=false assignStorageRoles=false`. Once both slots use managed identity, the template removes the legacy setting and disables shared-key authorization without duplicating the manually created role assignment.

New deployments leave shared-key authorization disabled from the start.

## Publish and deploy to staging

Publish a Release package:

```powershell
$publish = Join-Path $env:TEMP "orleans-shopping-cart-publish"
$package = Join-Path $env:TEMP "orleans-shopping-cart.zip"

dotnet publish `
  .\samples\Deployment\AzureAppService\Silo\Orleans.ShoppingCart.Silo.csproj `
  --configuration Release `
  --framework net10.0 `
  --output $publish

Compress-Archive -Path "$publish\*" -DestinationPath $package -Force
```

Deploy it to staging:

```azurecli
az webapp deploy `
  --name $appName `
  --resource-group $resourceGroup `
  --slot "${appName}stg" `
  --type zip `
  --src-path $package `
  --clean true `
  --restart true
```

Wait for:

```text
https://<staging-default-hostname>/health/ready
```

The exact default hostname is also available in the Bicep deployment outputs.

## Swap into production

After staging is healthy:

```azurecli
az webapp deployment slot swap `
  --name $appName `
  --resource-group $resourceGroup `
  --slot "${appName}stg" `
  --target-slot production
```

`ORLEANS_CLUSTER_ID` is a deployment-slot setting. During a swap, App Service applies the target slot's settings to staging and warms every staging instance before switching traffic. The new silos therefore join the production Orleans cluster before cutover.

This is a rolling upgrade: old and new silos coexist temporarily. Grain interfaces, serialized payloads, state, and external behavior must be mutually compatible. After cutover, the previous production code moves to staging and is recycled with the staging cluster ID.

For an incompatible release, use a separate App Service app and cluster ID instead of a slot swap. Don't allow incompatible clusters to concurrently own the same mutable grain state.

## Configure GitHub Actions with OIDC

The sample includes [`infra/deploy.yml`](https://github.com/dotnet/orleans/blob/main/samples/Deployment/AzureAppService/infra/deploy.yml). Copy it to `.github/workflows/deploy-app-service.yml` in your repository.

Create a federated deployment identity for the GitHub `production` environment by following [Connect GitHub and Azure](/azure/developer/github/connect-from-azure). Don't create an `--sdk-auth` client secret or store an Azure credential JSON document.

Configure these GitHub environment variables:

| Variable | Purpose |
| --- | --- |
| `AZURE_APP_NAME` | Globally unique App Service name |
| `AZURE_CLIENT_ID` | Federated deployment identity client ID |
| `AZURE_RESOURCE_GROUP_LOCATION` | Azure region |
| `AZURE_RESOURCE_GROUP_NAME` | Existing target resource group |
| `AZURE_SUBSCRIPTION_ID` | Subscription ID |
| `AZURE_TENANT_ID` | Microsoft Entra tenant ID |

The workflow pins its actions to immutable commit SHAs and grants only `contents: read` and `id-token: write`. Its Azure identity doesn't need `roleAssignments/write` after the manual bootstrap. It:

1. Uses the repository `global.json`.
1. Publishes and packages the .NET 10 app.
1. Signs in to Azure using a short-lived OIDC token.
1. Deploys the Bicep template.
1. Deploys to staging and waits for readiness.
1. Swaps staging into production.
1. Verifies production readiness.

Use a protected GitHub environment to require deployment approval and restrict allowed branches.

## Health and graceful shutdown

App Service uses `/health/ready` for startup warm-up and Health check. The endpoint returns success only after the .NET host and Orleans silo start, and it becomes unavailable when host shutdown begins.

`/health/live` is a cheap local process check. The sample doesn't make liveness depend on Azure Storage; a shared dependency outage must not restart every instance simultaneously.

The template configures:

- `WEBSITE_WARMUP_PATH=/health/ready`
- `WEBSITE_WARMUP_STATUSES=200`
- `WEBSITE_SWAP_WARMUP_PING_PATH=/health/ready`
- `WEBSITE_SWAP_WARMUP_PING_STATUSES=200`
- `WEBSITE_HEALTHCHECK_MAXPINGFAILURES=2`

The .NET host allows up to 30 seconds for Orleans to leave membership. App Service doesn't guarantee that every worker receives that entire interval and can stop a worker abruptly, so application correctness must tolerate unknown call outcomes and silo loss.

## Security and observability

The deployment enforces HTTPS, TLS 1.2 or later, disables FTPS, and keeps storage account keys disabled. Storage accepts network traffic only from the two integration subnets and authorizes each slot using its managed identity.

Application Insights receives ASP.NET Core request, dependency, exception, and application log telemetry. Orleans logs use `Microsoft.Extensions.Logging` and include a per-worker role instance based on `WEBSITE_INSTANCE_ID`. Add Orleans `System.Diagnostics.Metrics` instruments to an OpenTelemetry pipeline when metric export is required; see [Orleans observability](../host/monitoring/index.md).

Monitor:

- Ready App Service and Orleans silo counts.
- Membership joins, departures, and suspected silos.
- Request latency, failures, rejections, and load shedding.
- Storage authorization, throttling, and latency.
- Worker CPU, memory, restarts, and deployment-slot operations.

## Understand grain and serializer identities

The shopping cart calls:

```csharp
grainFactory.GetGrain<IProductGrain>(product.Id)
```

The `product.Id` argument is the product grain's string identity. In contrast, `[Id(0)]`, `[Id(1)]`, and the other <xref:Orleans.IdAttribute> annotations on `ProductDetails` identify serialized fields for version-tolerant Orleans serialization. They don't define grain identity and shouldn't be renumbered after deployment.

## Production considerations

- Scale to at least three workers and preserve spare capacity during upgrades.
- Keep client affinity enabled for the cohosted Blazor Server UI; Orleans doesn't require HTTP affinity.
- Size virtual network integration subnets for planned scale plus worker replacement.
- External Orleans clients need private network reachability to every gateway address.
- Validate scale-out, scale-in, worker replacement, role assignment propagation, and slot rollback under production-like load.
- Back up durable grain state independently from Orleans membership data.

For broader operational guidance, see [Production-readiness checklist](production-readiness.md), [Topology, networking, and clustering](networking.md), and [Graceful shutdown and upgrades](upgrades.md).
