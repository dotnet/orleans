# Orleans shopping cart on Azure App Service

This sample deploys a multi-instance Orleans shopping cart to either Windows or Linux [Azure App Service](https://learn.microsoft.com/azure/app-service/overview). It demonstrates:

- Three App Service workers running a cohosted ASP.NET Core app and Orleans silo.
- Per-instance Orleans endpoints discovered from `WEBSITE_PRIVATE_IP` and `WEBSITE_PRIVATE_PORTS`.
- Separate production and staging virtual network integration subnets.
- Azure Table Storage clustering and grain state using a user-assigned managed identity.
- App Service Health check, startup warm-up, graceful shutdown, and slot-based rollout.
- Microsoft Entra authentication and app-role authorization for product administration.
- Application Insights logging and request/dependency telemetry.
- GitHub Actions deployment using OpenID Connect (OIDC) instead of a deployment client secret.

> [!IMPORTANT]
> App Service HTTP scale-out isn't sufficient for Orleans by itself. The sample allocates one private silo port per instance and uses regional virtual network integration so silos can connect directly over TCP.

## Choose a platform

Both entry points call the shared modules under `infra/flex` and deploy the same application:

| Platform | Template | Platform-specific configuration |
| --- | --- | --- |
| Windows | `infra/windows/main.bicep` | Windows App Service plan and `netFrameworkVersion` |
| Linux | `infra/linux/main.bicep` | Linux plan with `reserved: true` and `linuxFxVersion` |

Both platforms advertise the dynamically allocated private address and ports while listening on all local interfaces. Each worker uses the read-only `WEBSITE_INSTANCE_ID` as its Orleans silo name for diagnostics. Before using either topology in production, scale it out and verify direct TCP reachability between every advertised silo endpoint.

ZIP deployment places the published files in `D:\home\site\wwwroot` on Windows and `/home/site/wwwroot` on Linux. The application doesn't depend on either path or persist grain state there; durable state is stored in Azure Table Storage.

## Run locally

The repository's `global.json` selects the required .NET SDK.

```powershell
dotnet run `
  --project .\Silo\Orleans.ShoppingCart.Silo.csproj `
  --environment ASPNETCORE_ENVIRONMENT=Development
```

Development mode intentionally uses localhost clustering and in-memory state. Every other environment requires App Service's private endpoint variables and Azure Storage configuration; it never falls back to a single-host production cluster.

The storefront remains available locally, but product management is unavailable because local execution doesn't have the trusted `X-MS-CLIENT-PRINCIPAL` header injected by App Service Authentication.

## Configure Microsoft Entra authentication

Create a Microsoft Entra app registration for [App Service Authentication](https://learn.microsoft.com/azure/app-service/overview-authentication-authorization):

1. Add an app role with value `ProductAdministrator` and allow users or groups.
1. Create a client secret and record its value.
1. Assign the app role to product administrators through the enterprise application.
1. Add production and staging Web redirect URIs ending in `/.auth/login/aad/callback`.

The exact production and staging hostnames are deployment outputs. You can deploy the infrastructure first, add those redirect URIs, and then deploy the application.

App Service Authentication allows anonymous storefront traffic. The application trusts only the platform-injected principal header, protects the `/products` route, hides its navigation item from unauthorized users, and repeats authorization in `ProductService` before mutating grain state. The silo disables its Orleans client gateway, so catalog mutations can't bypass those checks through a direct Orleans client connection.

## Deploy manually

Install the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli), sign in, and choose an operating system:

```powershell
$location = "westus3"
$resourceGroup = "orleans-shopping-cart"
$appName = "<globally-unique-lowercase-name-up-to-16-characters>"
$operatingSystem = "linux" # Use "windows" for Windows App Service.
$authenticationTenantId = "<microsoft-entra-tenant-id>"
$authenticationClientId = "<app-registration-client-id>"
$authenticationClientSecret = "<app-registration-client-secret>"

az login
az group create --name $resourceGroup --location $location
```

For Linux, confirm that the target region offers the configured built-in .NET runtime:

```powershell
az webapp list-runtimes --os linux |
  Select-String "DOTNETCORE"
```

Deploy the selected template:

```powershell
az deployment group create `
  --resource-group $resourceGroup `
  --template-file ".\infra\$operatingSystem\main.bicep" `
  --parameters `
    appName=$appName `
    location=$location `
    authenticationTenantId=$authenticationTenantId `
    authenticationClientId=$authenticationClientId `
    authenticationClientSecret=$authenticationClientSecret
```

The secret parameter is marked `@secure()` so Azure doesn't retain its value in deployment history. For production, rotate it before expiration and prefer a Key Vault reference or separate app registrations for production and staging.

The initial deployment is a bootstrap operation. Its identity needs permission to create resources and role assignments. The template grants the application's user-assigned managed identity **Storage Table Data Contributor** on the sample storage account.

Publish and deploy to staging:

```powershell
$publish = Join-Path $env:TEMP "orleans-shopping-cart-publish"
$package = Join-Path $env:TEMP "orleans-shopping-cart.zip"

dotnet publish .\Silo\Orleans.ShoppingCart.Silo.csproj `
  --configuration Release `
  --framework net10.0 `
  --output $publish

Compress-Archive -Path "$publish\*" -DestinationPath $package -Force

$stagingSlot = "${appName}stg"

az webapp deploy `
  --name $appName `
  --resource-group $resourceGroup `
  --slot $stagingSlot `
  --type zip `
  --src-path $package `
  --clean true `
  --restart true
```

Read the exact staging hostname and verify readiness:

```powershell
$stagingHost = az webapp show `
  --name $appName `
  --resource-group $resourceGroup `
  --slot $stagingSlot `
  --query defaultHostName `
  --output tsv

Invoke-WebRequest "https://$stagingHost/health/ready"
```

Add both callback URLs to the app registration, then swap staging into production:

```powershell
az webapp deployment slot swap `
  --name $appName `
  --resource-group $resourceGroup `
  --slot $stagingSlot `
  --target-slot production
```

Managed identity role assignments can take several minutes to propagate after the first infrastructure deployment.

## Configure GitHub Actions

Complete the [manual infrastructure deployment](#deploy-manually) once with its default `assignStorageRoles=true` before using the routine workflow. The checked-in workflow deliberately passes `assignStorageRoles=false` so its OIDC identity doesn't need permission to create role assignments; it isn't a bootstrap workflow.

Copy [`infra/deploy.yml`](infra/deploy.yml) to `.github/workflows/deploy-app-service.yml`.

Configure [GitHub OIDC federation for Azure](https://learn.microsoft.com/azure/developer/github/connect-from-azure-openid-connect) for the `production` environment and add these environment variables:

| Variable | Value |
| --- | --- |
| `AUTHENTICATION_CLIENT_ID` | App Service Authentication app-registration client ID |
| `AUTHENTICATION_TENANT_ID` | Microsoft Entra tenant ID for user authentication |
| `AZURE_APP_NAME` | Globally unique App Service name |
| `AZURE_APP_SERVICE_OS` | `windows` or `linux` |
| `AZURE_CLIENT_ID` | Client ID of the federated deployment identity |
| `AZURE_RESOURCE_GROUP_LOCATION` | Azure region |
| `AZURE_RESOURCE_GROUP_NAME` | Existing target resource group |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription ID |
| `AZURE_TENANT_ID` | Microsoft Entra tenant ID for Azure deployment |

Add `AUTHENTICATION_CLIENT_SECRET` as a GitHub environment secret. This secret is for App Service user authentication; GitHub's Azure deployment uses OIDC and doesn't use a client secret.

The workflow requests only `contents: read` and `id-token: write`. It deploys with `assignStorageRoles=false`, so its Azure identity doesn't need permission to create role assignments after bootstrap. Grant it **Contributor** on the target resource group, or preferably a custom role limited to the resources in the Bicep template.

## Upgrade an existing Windows deployment

The Windows template preserves the existing App Service plan and storage account names, the `ShoppingCartService` service ID, the `Default` and `Staging` cluster IDs, and existing membership and persistence table names.

If an existing deployment still uses a storage account key, don't run the full template first. Migrate in this order:

1. Create `${appName}-identity`, assign it to the app and `${appName}stg` slot, and grant it **Storage Table Data Contributor** on `${appName}storage`.
1. Merge `AZURE_CLIENT_ID`, `ORLEANS_AZURE_STORAGE_URI`, `ORLEANS_SERVICE_ID=ShoppingCartService`, and the existing slot-specific cluster IDs into both slots.
1. Deploy the managed-identity build to staging, wait for readiness, and swap it into production.
1. Keep shared-key access enabled for the rollback window because the former production build now runs in staging.
1. After both slots run the managed-identity build, deploy `infra/windows/main.bicep` with `allowSharedKeyAccess=false assignStorageRoles=false`.

Don't change an existing App Service plan between Windows and Linux. Deploy a separate app and migrate traffic instead.

## Operational behavior

- Production and staging use separate delegated subnets and slot-sticky cluster IDs.
- Both slots retain the stable `ShoppingCartService` service ID across deployments.
- Each cluster uses separate Azure Table membership and grain-state tables.
- The app returns readiness only after the host and silo start, and removes readiness before shutdown.
- The host allows up to 30 seconds for Orleans shutdown. App Service can terminate a worker sooner, so correctness must tolerate silo loss and unknown call outcomes.
- HTTPS and TLS settings protect HTTP ingress. Orleans private silo TCP traffic isn't encrypted by this sample; add Orleans TLS if the threat model requires transport encryption inside the virtual network.
- Application Insights receives ASP.NET Core, dependency, exception, application, and Orleans logs.
- Linux App Service runs Easy Auth in a sidecar container; Windows uses the in-process App Service module. Both inject the same principal header contract.
- `DOTNETCORE|10.0`, `v10.0`, and `net10.0` are deployment literals, not Orleans product-version branding.

For complete platform guidance, see:

- [Deploy Orleans to Azure App Service on Windows](https://dotnet.github.io/orleans/docs/deployment/deploy-to-azure-app-service/)
- [Deploy Orleans to Azure App Service on Linux](https://dotnet.github.io/orleans/docs/deployment/deploy-to-azure-app-service-linux/)

## Application model

The product ID passed to `GetGrain<IProductGrain>(product.Id)` is the product grain's identity. The `[Id(n)]` attributes on `ProductDetails` identify serialized fields for Orleans's version-tolerant serializer; they don't define grain identity.

## Acknowledgements

This sample derives from [IEvangelist/orleans-shopping-cart](https://github.com/IEvangelist/orleans-shopping-cart) and was imported from [Azure-Samples/Orleans-Cluster-on-Azure-App-Service](https://github.com/Azure-Samples/Orleans-Cluster-on-Azure-App-Service). Product-administration authorization incorporates the defense-in-depth design from [Azure-Samples PR #13](https://github.com/Azure-Samples/Orleans-Cluster-on-Azure-App-Service/pull/13). The original MIT license is preserved in this directory.
