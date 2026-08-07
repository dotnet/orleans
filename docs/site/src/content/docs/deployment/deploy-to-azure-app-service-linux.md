---
title: Deploy Orleans to Azure App Service on Linux
description: Deploy and operate an Orleans cluster on Linux Azure App Service using private instance endpoints and managed identity.
ms.date: 08/06/2026
ms.topic: tutorial
ms.custom: devops
---

# Deploy Orleans to Azure App Service on Linux

This tutorial deploys the [Orleans shopping cart sample](https://github.com/dotnet/orleans/tree/main/samples/Deployment/AzureAppService) to the [built-in .NET stack on Linux Azure App Service](https://learn.microsoft.com/azure/app-service/configure-language-dotnetcore). The application, storage, identity, networking, health, and OIDC design are shared with the [Windows guide](deploy-to-azure-app-service.md), but Linux plan, runtime, startup, and Easy Auth behavior require separate validation.

## Understand the Linux topology

The Bicep entry point `infra/linux/main.bicep` creates:

- A Premium v3 Linux plan with `kind: linux` and `reserved: true`.
- A Linux web app and staging slot using `linuxFxVersion: DOTNETCORE|10.0`.
- Three workers, each cohosting ASP.NET Core and an Orleans silo.
- Production and staging virtual network integration subnets.
- Azure Table Storage clustering and durable grain state authorized by managed identity.
- App Service Authentication, Application Insights, Log Analytics, health checks, and warm-up settings.

App Service's HTTP front end can't route Orleans silo-to-silo connections. Each worker reads `WEBSITE_PRIVATE_IP` and the first value in `WEBSITE_PRIVATE_PORTS`, then advertises that dynamically allocated silo endpoint. It uses the read-only `WEBSITE_INSTANCE_ID` as its Orleans silo name for diagnostics. It listens on all local interfaces because the advertised address isn't guaranteed to be locally bindable:

```csharp
siloBuilder.ConfigureEndpoints(
    privateIp,
    siloPort,
    gatewayPort: 0,
    listenOnAnyHostAddress: true);
```

The private address and ports are documented common App Service settings, not Windows-only settings. However, Microsoft doesn't document an Orleans-specific Linux mapping guarantee. Treat deployment validation as mandatory:

1. Scale to at least three workers.
1. Confirm every worker receives a distinct `WEBSITE_PRIVATE_IP` and at least one `WEBSITE_PRIVATE_PORTS` value.
1. Inspect Orleans membership and confirm that each silo advertises those values.
1. Test bidirectional TCP connections among every advertised silo endpoint.
1. Repeat during scale-out, scale-in, restart, and slot swap.

> [!IMPORTANT]
> Regional virtual network integration is primarily outbound connectivity. Don't assume that enabling it alone proves inbound private-port reachability.

The sample disables the Orleans client gateway because the web app uses the cohosted silo's local client. This prevents a private Orleans client from bypassing the Easy Auth and `ProductAdministrator` checks around catalog mutations. Orleans gateways aren't an end-user authentication boundary. If external Orleans clients are required, enable a second private port only for fully trusted application clients; keep untrusted callers behind an authenticated API.

## Linux-specific requirements

Use a dedicated Standard, Premium, or Isolated tier that supports virtual network integration and deployment slots. The sample uses Premium v3 and three workers.

Before deployment, query the target region's built-in Linux runtimes:

```azurecli
az webapp list-runtimes --os linux
```

Confirm that the output includes the .NET version represented by the template's `DOTNETCORE|10.0` literal. Runtime availability varies by cloud and region. If the token isn't available, update the mechanical runtime and target-framework literals together to a supported version.

Linux startup is container-based. The built-in .NET stack starts the framework-dependent ZIP deployment without a custom startup command. The template sets `SCM_DO_BUILD_DURING_DEPLOYMENT=false` so Oryx runs the prepublished binaries instead of restoring and rebuilding them. ZIP deployment extracts the publish output to the case-sensitive `/home/site/wwwroot` path, compared with `D:\home\site\wwwroot` on Windows. The sample doesn't depend on either location or store durable state on the App Service filesystem.

[App Service Authentication](https://learn.microsoft.com/azure/app-service/overview-authentication-authorization) runs as an ambassador sidecar rather than the Windows in-process module, but both platforms inject the same authenticated principal headers. `WEBSITE_WARMUP_PATH`, `WEBSITE_WARMUP_STATUSES`, and `WEBSITE_SWAP_WARMUP_PING_PATH` still apply. See the [App Service environment-variable reference](https://learn.microsoft.com/azure/app-service/reference-app-settings) for these settings. If startup legitimately exceeds the platform limit, adjust `WEBSITES_CONTAINER_START_TIME_LIMIT` within the supported range instead of returning readiness before Orleans starts.

## Prerequisites

- An Azure subscription and permission to create resources and role assignments.
- The [.NET SDK selected by `global.json`](https://dotnet.microsoft.com/download).
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) with Bicep.
- A clone of the [`dotnet/orleans`](https://github.com/dotnet/orleans) repository.
- A Microsoft Entra app registration and client secret for App Service Authentication.

Set deployment values:

```powershell
$location = "westus3"
$resourceGroup = "orleans-shopping-cart-linux"
$appName = "<globally-unique-lowercase-name-up-to-16-characters>"
$authenticationTenantId = "<microsoft-entra-tenant-id>"
$authenticationClientId = "<app-registration-client-id>"
$authenticationClientSecret = "<app-registration-client-secret>"
```

## Configure authentication and authorization

Create a `ProductAdministrator` app role in the App Service Authentication registration, assign it to product administrators, and add production/staging Web redirect URIs ending in `/.auth/login/aad/callback`.

The storefront allows anonymous traffic. Product management requires the app role at the route and navigation layers and rechecks it immediately before grain-state mutation. The authentication handler accepts only the bounded App-Service-injected Entra principal header. It doesn't validate tokens itself, so don't expose the app through a path that bypasses Easy Auth.

The exact production and staging hostnames are Bicep outputs. You can add their callback URIs after provisioning and before signing in.

## Deploy the Linux infrastructure

```azurecli
az login
az group create --name $resourceGroup --location $location

az deployment group create `
  --resource-group $resourceGroup `
  --template-file .\samples\Deployment\AzureAppService\infra\linux\main.bicep `
  --parameters `
    appName=$appName `
    location=$location `
    authenticationTenantId=$authenticationTenantId `
    authenticationClientId=$authenticationClientId `
    authenticationClientSecret=$authenticationClientSecret
```

The template requests one private silo port per worker, assigns production and staging to separate delegated `/24` subnets, and restricts storage network access to those subnets. It disables storage shared-key authorization and grants the application's user-assigned identity **Storage Table Data Contributor**.

The first deployment needs resource creation and constrained role-assignment permission. Later deployments can use `assignStorageRoles=false`. Identity assignments can take several minutes to propagate.

The authentication secret is marked `@secure()` and stored as a slot-sticky app setting. Rotate it before expiration. Prefer Key Vault references and separate production/staging app registrations for a hardened deployment.

## Publish and deploy

The same framework-dependent package runs on Windows and Linux:

```powershell
$publish = Join-Path $env:TEMP "orleans-shopping-cart-linux-publish"
$package = Join-Path $env:TEMP "orleans-shopping-cart-linux.zip"

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

Wait for the staging output hostname at `/health/ready`, add both callback URLs to the app registration, and swap:

```azurecli
az webapp deployment slot swap `
  --name $appName `
  --resource-group $resourceGroup `
  --slot "${appName}stg" `
  --target-slot production
```

Both slots retain the stable `ShoppingCartService` service ID. `ORLEANS_CLUSTER_ID` is slot-sticky, and each cluster has separate membership and persistence tables. The staging workers receive production settings and warm up into the production Orleans cluster before HTTP traffic switches. Old and new silos coexist temporarily, so releases must remain wire-, serializer-, state-, and behavior-compatible.

For incompatible releases, deploy a separate app and cluster ID. Don't switch an existing App Service plan from Windows to Linux and don't allow incompatible clusters to own the same mutable state.

## Deploy with GitHub OIDC

Copy [`infra/deploy.yml`](https://github.com/dotnet/orleans/blob/main/samples/Deployment/AzureAppService/infra/deploy.yml). Configure a protected `production` environment with required reviewers and restricted deployment branches:

| Name | Kind | Value |
| --- | --- | --- |
| `AUTHENTICATION_CLIENT_ID` | Variable | Easy Auth app-registration client ID |
| `AUTHENTICATION_CLIENT_SECRET` | Secret | Easy Auth app-registration client secret |
| `AUTHENTICATION_TENANT_ID` | Variable | Tenant for user authentication |
| `AZURE_APP_NAME` | Variable | Globally unique app name |
| `AZURE_APP_SERVICE_OS` | Variable | `linux` |
| `AZURE_CLIENT_ID` | Variable | Federated deployment identity client ID |
| `AZURE_RESOURCE_GROUP_LOCATION` | Variable | Azure region |
| `AZURE_RESOURCE_GROUP_NAME` | Variable | Existing resource group |
| `AZURE_SUBSCRIPTION_ID` | Variable | Subscription ID |
| `AZURE_TENANT_ID` | Variable | Tenant for Azure deployment |

The Azure deployment identity uses OIDC, not a stored Azure credential. The Easy Auth secret has a separate purpose and is passed as a secure Bicep parameter. The routine identity doesn't need role-assignment permission; grant Contributor on the resource group or a narrower custom deployment role. The workflow deploys the Linux entry point, publishes to staging, waits for readiness, swaps, and verifies production.

## Health, shutdown, and observability

- `/health/ready` returns success only after the .NET host and Orleans silo start and becomes unavailable before shutdown.
- `/health/live` checks only the local process and doesn't restart all workers during a shared storage outage.
- The host allows up to 30 seconds for Orleans to leave membership. Linux App Service can still stop a container sooner, so grain calls must tolerate unknown outcomes and silo loss.
- Application Insights collects ASP.NET Core request, dependency, exception, application, and Orleans logs. Include `WEBSITE_INSTANCE_ID`, cluster ID, slot, and deployment metadata in operational queries.
- Monitor ready worker/silo counts, membership changes, container starts and restarts, CPU/memory, request latency, Orleans rejections, storage failures, and slot operations.

## Security and platform constraints

The template requires HTTPS, TLS 1.2 or later, disables FTPS, and uses managed identity for storage without account keys. Client affinity remains enabled for Blazor Server; Orleans doesn't depend on HTTP affinity.

HTTPS terminates at the App Service front end. The sample's private Orleans silo connections aren't encrypted. Add Orleans TLS if the virtual network isn't a sufficient trust boundary.

App Service can replace workers without delivering the full shutdown interval. Size integration subnets for planned scale plus replacement capacity, preserve at least three workers, and validate failure recovery and rolling upgrades under load. Back up durable grain state independently from membership data.

For shared operational guidance, see [Production-readiness checklist](production-readiness.md), [Topology, networking, and clustering](networking.md), [Health and observability](health-and-observability.md), and [Graceful shutdown and upgrades](upgrades.md).
