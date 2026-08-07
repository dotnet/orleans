---
title: Deploy Orleans to Azure App Service on Windows
description: Deploy and operate an Orleans cluster on Windows Azure App Service using private instance endpoints and managed identity.
ms.date: 08/06/2026
ms.topic: tutorial
ms.custom: devops
---

# Deploy Orleans to Azure App Service on Windows

This tutorial deploys the [Orleans shopping cart sample](https://github.com/dotnet/orleans/tree/main/samples/Deployment/AzureAppService) as a multi-instance cluster on [Windows Azure App Service](https://learn.microsoft.com/azure/app-service/overview). For Linux plan, runtime, startup, and Easy Auth differences, see [Deploy Orleans to Azure App Service on Linux](deploy-to-azure-app-service-linux.md).

The sample cohosts an ASP.NET Core Blazor app and an Orleans silo in each worker. It uses:

- A three-worker Premium v3 Windows App Service plan.
- Regional virtual network integration and one private silo TCP port per instance.
- Azure Table Storage for Orleans membership and grain state.
- A user-assigned managed identity with Microsoft Entra authorization and no storage account keys.
- App Service Authentication and a `ProductAdministrator` app role for catalog changes.
- Separate production and staging slot clusters.
- Health check, startup warm-up, bounded graceful shutdown, Application Insights, and Log Analytics.
- A GitHub Actions workflow authenticated to Azure using OpenID Connect (OIDC).

## Understand the topology

Orleans silos communicate directly with individual silos over long-lived TCP connections. The App Service HTTP load balancer doesn't provide this connectivity.

[Regional virtual network integration](https://learn.microsoft.com/azure/app-service/overview-vnet-integration) gives each worker a private address in `WEBSITE_PRIVATE_IP`. With `vnetPrivatePortsCount: 1`, App Service exposes a dynamically allocated port in `WEBSITE_PRIVATE_PORTS`. The read-only `WEBSITE_INSTANCE_ID` becomes the Orleans silo name for diagnostics. See the [App Service environment-variable reference](https://learn.microsoft.com/azure/app-service/reference-app-settings) for the platform settings. The application:

1. Advertises the private address and allocated silo port.
1. Listens on all local interfaces because the advertised address might not be locally bindable.
1. Disables the Orleans client gateway because the cohosted web application uses the silo's local client.
1. Uses Azure Table Storage as the external clustering provider and durable grain store.
1. Integrates production and staging with separate delegated subnets.

```csharp
siloBuilder.ConfigureEndpoints(
    privateIp,
    siloPort,
    gatewayPort: 0,
    listenOnAnyHostAddress: true);
```

Every silo must reach every advertised silo endpoint. Disabling the gateway also ensures that catalog mutations enter through the Easy Auth-protected web application instead of an unauthenticated Orleans client connection.

Orleans gateways aren't an end-user authentication boundary. If the application needs external Orleans clients, enable and advertise a second private port only for fully trusted application clients. Keep untrusted clients behind an authenticated API, and enforce authorization at the operation that mutates grain state.

> [!IMPORTANT]
> Virtual network integration is primarily an outbound feature. Validate the private-port behavior on a scaled-out plan before production: confirm that every worker receives a private silo port and can connect to every advertised endpoint.

Use a dedicated tier that supports virtual network integration and deployment slots. Size each integration subnet for at least twice the planned maximum scale; `/26` is a practical minimum for a new deployment with room for replacement workers.

## Prerequisites

- An Azure subscription and permission to create resources and role assignments.
- The [.NET SDK selected by `global.json`](https://dotnet.microsoft.com/download).
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) with Bicep.
- A clone of the [`dotnet/orleans`](https://github.com/dotnet/orleans) repository.
- A Microsoft Entra app registration for [App Service Authentication](https://learn.microsoft.com/azure/app-service/overview-authentication-authorization).

Set local deployment values:

```powershell
$location = "westus3"
$resourceGroup = "orleans-shopping-cart"
$appName = "<globally-unique-lowercase-name-up-to-16-characters>"
$authenticationTenantId = "<microsoft-entra-tenant-id>"
$authenticationClientId = "<app-registration-client-id>"
$authenticationClientSecret = "<app-registration-client-secret>"
```

## Configure user authentication

In the App Service Authentication app registration:

1. Add an app role whose value is `ProductAdministrator` and whose allowed member types include users or groups.
1. Create a client secret.
1. Assign the role to product administrators through the enterprise application.
1. Add production and staging Web redirect URIs ending in `/.auth/login/aad/callback`.

The exact hostnames are deployment outputs, so you can add the redirect URIs after provisioning and before users sign in.

Easy Auth allows anonymous storefront requests and injects authenticated claims in `X-MS-CLIENT-PRINCIPAL`. On a non-container Windows app, the platform authentication component is a native IIS module. The application accepts only an Entra (`aad`) principal, limits and parses the header, protects the `/products` route, hides unauthorized navigation, and reauthorizes every product mutation in the service layer.

The parser trusts App Service to validate tokens and remove spoofed external identity headers. Don't reuse it behind an untrusted reverse proxy or expose a bypass route to the application process.

## Run locally

```powershell
dotnet run `
  --project .\samples\Deployment\AzureAppService\Silo\Orleans.ShoppingCart.Silo.csproj `
  --environment ASPNETCORE_ENVIRONMENT=Development
```

Development uses localhost clustering and in-memory state. Every nondevelopment environment requires `WEBSITE_PRIVATE_IP`, `WEBSITE_PRIVATE_PORTS`, `ORLEANS_SERVICE_ID`, `ORLEANS_CLUSTER_ID`, `ORLEANS_AZURE_STORAGE_URI`, and `AZURE_CLIENT_ID`. Missing production configuration fails startup.

The anonymous storefront works locally. Product management remains unavailable because local execution doesn't have an App-Service-trusted principal header.

## Deploy the infrastructure

Sign in, create a resource group, and deploy the Windows entry point:

```azurecli
az login
az group create --name $resourceGroup --location $location

az deployment group create `
  --resource-group $resourceGroup `
  --template-file .\samples\Deployment\AzureAppService\infra\windows\main.bicep `
  --parameters `
    appName=$appName `
    location=$location `
    authenticationTenantId=$authenticationTenantId `
    authenticationClientId=$authenticationClientId `
    authenticationClientSecret=$authenticationClientSecret
```

The template creates:

- A three-worker Premium v3 Windows plan and production/staging slots.
- A virtual network with separate `/24` delegated subnets.
- A storage account restricted to those subnets, with shared-key authorization disabled.
- A shared [user-assigned managed identity](https://learn.microsoft.com/azure/app-service/overview-managed-identity) and **Storage Table Data Contributor** assignment.
- App Service Authentication for both slots.
- Log Analytics and workspace-based Application Insights.

The authentication secret is a secure Bicep parameter and a slot-sticky app setting. Rotate it before expiration. For production, prefer a Key Vault reference and separate app registrations for production and staging.

The first deployment is a privileged bootstrap because it creates a data-plane role assignment. Routine deployments pass `assignStorageRoles=false` and don't need `roleAssignments/write`.

Managed identity assignments can take several minutes to propagate. A first application start can fail until the identity can access table data.

## Publish and deploy to staging

```powershell
$publish = Join-Path $env:TEMP "orleans-shopping-cart-publish"
$package = Join-Path $env:TEMP "orleans-shopping-cart.zip"

dotnet publish `
  .\samples\Deployment\AzureAppService\Silo\Orleans.ShoppingCart.Silo.csproj `
  --configuration Release `
  --framework net10.0 `
  --output $publish

Compress-Archive -Path "$publish\*" -DestinationPath $package -Force

az webapp deploy `
  --name $appName `
  --resource-group $resourceGroup `
  --slot "${appName}stg" `
  --type zip `
  --src-path $package `
  --clean true `
  --restart true
```

The ZIP contains the contents of the publish directory, not the directory itself. App Service extracts it to `D:\home\site\wwwroot`. The sample doesn't write durable data there; Orleans grain state remains in Azure Table Storage.

Add the production and staging callback URLs to the app registration. Then wait for the staging deployment output hostname at:

```text
https://<staging-default-hostname>/health/ready
```

## Swap into production

Review [App Service deployment slot behavior](https://learn.microsoft.com/azure/app-service/deploy-staging-slots) before using a slot swap for an Orleans rollout.

```azurecli
az webapp deployment slot swap `
  --name $appName `
  --resource-group $resourceGroup `
  --slot "${appName}stg" `
  --target-slot production
```

Both slots keep the stable `ShoppingCartService` service ID. `ORLEANS_CLUSTER_ID` is slot-sticky, and each cluster uses separate membership and persistence tables. During a swap, App Service applies production settings to staging and warms every staging worker before switching traffic. The new silos join the production cluster before cutover.

Old and new silos coexist temporarily. Grain interfaces, serialized payloads, state, and external behavior must be mutually compatible. For an incompatible release, deploy a separate app and cluster ID; don't allow incompatible clusters to own the same mutable state.

## Configure GitHub Actions with OIDC

Copy the sample [`infra/deploy.yml`](https://github.com/dotnet/orleans/blob/main/samples/Deployment/AzureAppService/infra/deploy.yml) to `.github/workflows/deploy-app-service.yml`.

Create a federated deployment identity for a protected GitHub `production` environment. Require appropriate reviewers, restrict deployment branches, and don't create an `--sdk-auth` credential or store an Azure credential JSON document.

Configure these GitHub environment variables:

| Variable | Purpose |
| --- | --- |
| `AUTHENTICATION_CLIENT_ID` | App Service Authentication client ID |
| `AUTHENTICATION_TENANT_ID` | Tenant for user authentication |
| `AZURE_APP_NAME` | Globally unique App Service name |
| `AZURE_APP_SERVICE_OS` | `windows` |
| `AZURE_CLIENT_ID` | Federated deployment identity client ID |
| `AZURE_RESOURCE_GROUP_LOCATION` | Azure region |
| `AZURE_RESOURCE_GROUP_NAME` | Existing target resource group |
| `AZURE_SUBSCRIPTION_ID` | Subscription ID |
| `AZURE_TENANT_ID` | Tenant for Azure deployment |

Add `AUTHENTICATION_CLIENT_SECRET` as a protected environment secret. It belongs to the Easy Auth app registration; Azure deployment still uses short-lived OIDC credentials.

The workflow pins actions to immutable commit SHAs, grants only `contents: read` and `id-token: write`, deploys the selected Bicep entry point, publishes to staging, waits for readiness, swaps, and verifies production. Its routine Azure identity doesn't need role-assignment permission; grant Contributor on the target resource group or, preferably, a custom role limited to the resources that the template updates.

## Health and shutdown

App Service uses `/health/ready` for startup warm-up, slot warm-up, and [Health check](https://learn.microsoft.com/azure/app-service/monitor-instances-health-check). It succeeds only after the host and silo start, then becomes unavailable when shutdown begins. `/health/live` is a cheap local process check and deliberately doesn't depend on shared storage.

The host allows up to 30 seconds for Orleans to leave membership. App Service can terminate a worker sooner, so application correctness must tolerate silo loss and unknown call outcomes.

## Security and observability

The deployment enforces HTTPS, TLS 1.2 or later, disables FTPS, disables storage shared-key authorization, and restricts storage network access to the integration subnets. Managed identity authorizes table access.

HTTPS protects the public web endpoint. The private Orleans silo connections aren't encrypted by this sample. Use network controls and configure Orleans TLS when the threat model requires encryption inside the virtual network.

Application Insights receives request, dependency, exception, application, and Orleans logs. Monitor ready worker and silo counts, membership changes, latency, rejections, storage authorization/throttling, worker resources, restarts, and slot operations.

## Production considerations

- Keep at least three workers and spare capacity during upgrades.
- Keep HTTP client affinity for the cohosted Blazor Server UI; Orleans doesn't require HTTP affinity.
- Validate scale-out, scale-in, worker replacement, rollback, identity propagation, and private endpoint reachability under load.
- Back up durable grain state independently from membership data.
- Don't convert an existing App Service plan between Windows and Linux. Deploy a separate app and migrate traffic.

For broader guidance, see [Production-readiness checklist](production-readiness.md), [Topology, networking, and clustering](networking.md), and [Graceful shutdown and upgrades](upgrades.md).
