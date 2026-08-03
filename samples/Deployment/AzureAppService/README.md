# Orleans shopping cart on Azure App Service

This Orleans 10 sample deploys a multi-instance shopping cart to Azure App Service. It demonstrates:

- Three Windows App Service workers running a cohosted ASP.NET Core app and Orleans silo.
- Per-instance Orleans endpoints using `WEBSITE_PRIVATE_IP` and `WEBSITE_PRIVATE_PORTS`.
- Production and staging clusters on separate virtual network integration subnets.
- Azure Table Storage clustering and grain state using one dedicated user-assigned managed identity shared by both slots.
- App Service health checks, startup warm-up, graceful shutdown, and slot-based rollout.
- Application Insights logging and request/dependency telemetry.
- GitHub Actions deployment using OpenID Connect instead of a client secret.

> [!IMPORTANT]
> App Service HTTP scale-out isn't sufficient for Orleans by itself. The sample allocates two private ports per instance and uses regional virtual network integration so silos can connect directly over TCP.

## Run locally

The repository's `global.json` selects the required .NET SDK.

```powershell
dotnet run `
  --project .\Silo\Orleans.ShoppingCart.Silo.csproj `
  --environment ASPNETCORE_ENVIRONMENT=Development
```

The Development environment intentionally uses localhost clustering and in-memory state. Any other environment requires App Service's private endpoint variables and Azure Storage configuration; it never falls back to localhost clustering.

## Deploy manually

Install the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli), sign in, and create a resource group:

```powershell
$location = "westus3"
$resourceGroup = "orleans-shopping-cart"
$appName = "<globally-unique-lowercase-name-up-to-16-characters>"

az login
az group create --name $resourceGroup --location $location
```

Deploy the infrastructure:

```powershell
az deployment group create `
  --resource-group $resourceGroup `
  --template-file .\infra\flex\main.bicep `
  --parameters appName=$appName location=$location
```

This initial deployment is a bootstrap operation. Its identity needs permission to create resources and role assignments in the resource group. The template grants each slot's stable user-assigned managed identity **Storage Table Data Contributor** on the sample storage account.

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

Then swap staging into production:

```powershell
az webapp deployment slot swap `
  --name $appName `
  --resource-group $resourceGroup `
  --slot $stagingSlot `
  --target-slot production
```

Managed identity role assignments can take several minutes to propagate after the first infrastructure deployment.

## Configure GitHub Actions

Copy [`infra/deploy.yml`](infra/deploy.yml) to `.github/workflows/deploy-app-service.yml`.

Configure GitHub OpenID Connect federation for the `production` environment and add these environment variables:

| Variable | Value |
| --- | --- |
| `AZURE_APP_NAME` | Globally unique App Service name |
| `AZURE_CLIENT_ID` | Client ID of the federated deployment identity |
| `AZURE_RESOURCE_GROUP_LOCATION` | Azure region |
| `AZURE_RESOURCE_GROUP_NAME` | Existing target resource group |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription ID |
| `AZURE_TENANT_ID` | Microsoft Entra tenant ID |

The workflow requests only `contents: read` and `id-token: write`. It deploys with `assignStorageRoles=false`, so its Azure identity doesn't need permission to create role assignments after bootstrap. Grant it **Contributor** on the target resource group, or preferably a custom role limited to the resource types in the Bicep template.

### Upgrade an existing sample deployment

The template preserves the original Windows plan name, `${appName}storage` account, `ShoppingCartService` service ID, `Default` and `Staging` cluster IDs, and the existing clustering and persistence table names.

If an existing deployment still uses a storage account key, don't run the new full template first: replacing the App Service settings would remove the legacy credential before the new build starts.

Migrate in this order:

1. Create `${appName}-identity`, assign it to the existing app and `${appName}stg` slot, and grant it **Storage Table Data Contributor** on `${appName}storage`.
1. Add `AZURE_CLIENT_ID`, `ORLEANS_AZURE_STORAGE_URI`, `ORLEANS_SERVICE_ID=ShoppingCartService`, and the existing slot-specific cluster IDs using `az webapp config appsettings set`. That command merges these values and leaves the legacy connection-string setting in place.
1. Manually deploy the managed-identity build to `${appName}stg`, wait for readiness, and swap it into production.
1. Keep shared-key access enabled for the rollback window because the former production build now runs in staging.
1. After the rollback window, deploy and verify the managed-identity build in staging again.
1. Run the full Bicep deployment with `allowSharedKeyAccess=false assignStorageRoles=false`. At this point both slots use managed identity, and the template removes the legacy setting without duplicating the manually created role assignment.

## Operational behavior

- `ORLEANS_CLUSTER_ID` is slot-sticky. The staging and production slots remain separate clusters except during the controlled production warm-up phase of a swap.
- The same `ORLEANS_SERVICE_ID` identifies the application in both slots.
- Each cluster uses separate membership and grain-state tables.
- The app returns readiness only after the .NET host and Orleans silo start. Readiness turns off before Orleans shutdown.
- The .NET host allows up to 30 seconds for graceful Orleans shutdown. App Service can terminate a worker sooner, so correctness doesn't depend on graceful shutdown completing.
- The production and staging subnets are `/24` to leave room for worker replacement and platform upgrades.
- The storage account disables shared-key authentication and admits table traffic only through the two integration subnets.

For complete deployment and upgrade constraints, see [Deploy Orleans to Azure App Service](https://aka.ms/orleans-on-app-service).

## Application model

The product ID passed to `GetGrain<IProductGrain>(product.Id)` is the product grain's identity. The `[Id(n)]` attributes on `ProductDetails` identify serialized fields for Orleans's version-tolerant serializer; they don't define grain identity.

## Acknowledgements

This sample derives from [IEvangelist/orleans-shopping-cart](https://github.com/IEvangelist/orleans-shopping-cart) and was imported from [Azure-Samples/Orleans-Cluster-on-Azure-App-Service](https://github.com/Azure-Samples/Orleans-Cluster-on-Azure-App-Service). The original MIT license is preserved in this directory.
