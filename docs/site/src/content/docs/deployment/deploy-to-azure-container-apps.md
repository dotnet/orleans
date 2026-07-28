---
title: Deploy Orleans to Azure Container Apps
description: Learn how to deploy an updated Orleans shopping cart app to Azure Container Apps.
ms.date: 05/23/2025
ms.topic: tutorial
ms.custom:
  - devx-track-bicep
  - sfi-image-nochange
  - sfi-ropc-nochange
---

# Deploy Orleans to Azure Container Apps

In this tutorial, learn how to deploy an example Orleans shopping cart application to Azure Container Apps. This tutorial expands the functionality of the [sample Orleans shopping cart app](https://github.com/Azure-Samples/Orleans-Cluster-on-Azure-App-Service), introduced in [Deploy Orleans to Azure App Service](deploy-to-azure-app-service.md). The sample app adds Azure Active Directory (AAD) business-to-consumer (B2C) authentication and deploys to Azure Container Apps.

Learn how to deploy using GitHub Actions, the .NET and Azure CLIs, and Azure Bicep. Additionally, learn how to configure the Container App's HTTP ingress.

In this tutorial, you learn how to:

> [!div class="checklist"]
>
> - Deploy an Orleans application to Azure Container Apps
> - Automate deployment using GitHub Actions and Azure Bicep
> - Configure HTTP ingress

## Deploy with Aspire (Recommended)

If your Orleans application uses [Aspire](../host/aspire-integration.md), you can deploy to Azure Container Apps with a simplified workflow using the Aspire CLI (`aspire`). The Aspire CLI automatically provisions all required infrastructure and handles the deployment.

### Aspire deployment prerequisites

- [Aspire CLI](https://aspire.dev/get-started/install-cli/) installed
- [Azure CLI](/cli/azure/install-azure-cli) installed
- An Azure subscription with permissions to create resources
- Docker Desktop or Podman running (for building container images)

#### Install the Aspire CLI

### [Windows](#tab/windows)

```powershell
irm https://aspire.dev/install.ps1 | iex
```

### [macOS/Linux](#tab/macos-linux)

```bash
curl -sSL https://aspire.dev/install.sh | bash
```

---

### Enable the deploy command

The `aspire deploy` command is currently in preview and must be explicitly enabled:

### [Windows](#tab/windows)

```powershell
$env:DOTNET_ASPIRE_ENABLE_DEPLOY_COMMAND="true"
```

### [macOS/Linux](#tab/macos-linux)

```bash
export DOTNET_ASPIRE_ENABLE_DEPLOY_COMMAND=true
```

---

### Authenticate and deploy

1. **Sign in to Azure**:

   ```bash
   az login
   ```

2. **Deploy your application**:

   Navigate to your AppHost project directory and run:

   ```bash
   aspire deploy
   ```

   This single command performs the following steps:
   - Validates your configuration
   - Provisions Azure resources (Container Apps environment, Container Registry, storage, etc.)
   - Builds and pushes container images
   - Deploys your application

### Specify deployment parameters

You can provide deployment parameters inline:

```bash
aspire deploy --deployment-param location=eastus --deployment-param environment=production
```

Or use a parameters file for complex deployments:

```json
{
  "location": "eastus",
  "environment": "production",
  "resourceGroupName": "my-orleans-app-rg"
}
```

```bash
aspire deploy --deployment-params-file deployment-params.json
```

### What Aspire provisions automatically

When you deploy an Orleans Aspire application to Azure Container Apps, `aspire deploy` automatically provisions:

- **Azure Container Apps environment** - The hosting environment for your containers.
- **Azure Container Registry (ACR)** - For storing your container images.
- **Redis Cache** - If your Orleans cluster uses Redis for clustering, grain storage, or reminders.
- **Azure Storage** - If your Orleans cluster uses Azure Storage for clustering, grain storage, reminders, or streaming.
- **Azure Monitor / Application Insights** - For observability and distributed tracing.
- **Managed identities** - For secure, passwordless authentication between services.

> [!TIP]
> To update your app after code changes, simply run `aspire deploy` again. The CLI handles incremental updates efficiently.

For comprehensive guidance on deploying Aspire applications to Azure, see [Deploy Aspire to Azure Container Apps using the Aspire CLI](https://aspire.dev/deployment/azure/aca-deployment-aspire-cli/).

---

## Traditional deployment (without Aspire)

The following sections describe how to deploy Orleans to Azure Container Apps using GitHub Actions and Azure Bicep, without Aspire.

## Prerequisites

- A [GitHub account](https://github.com/join)
- [Read an introduction to Orleans](../overview.md)
- The [.NET 6 SDK](https://dotnet.microsoft.com/download/dotnet)
- The [Azure CLI](/cli/azure/install-azure-cli)
- A .NET integrated development environment (IDE)
  - Feel free to use [Visual Studio](https://visualstudio.microsoft.com) or [Visual Studio Code](https://code.visualstudio.com)

## Run the app locally

To run the app locally, fork the [Azure Samples: Orleans shopping cart on Azure Container Apps](https://github.com/Azure-Samples/orleans-blazor-server-shopping-cart-on-container-apps) repository and clone it to your local machine. Once cloned, open the solution in an IDE of your choice. If using Visual Studio, right-click the **Orleans.ShoppingCart.Silo** project, select **Set As Startup Project**, then run the app. Otherwise, run the app using the following .NET CLI command:

```dotnetcli
dotnet run --project Silo\Orleans.ShoppingCart.Silo.csproj
```

For more information, see [dotnet run](../../core/tools/dotnet-run.md). With the app running, a landing page discusses the app's functionality. In the upper-right corner, a sign-in button is visible. Sign up for an account or sign in if one already exists. Once signed in, navigate around and test its capabilities. All app functionality when running locally relies on in-memory persistence and local clustering. It also uses the [Bogus NuGet](https://www.nuget.org/packages/Bogus) package to generate fake products. Stop the app either by selecting the **Stop Debugging** option in Visual Studio or by pressing <kbd>Ctrl</kbd>+<kbd>C</kbd> in the .NET CLI.

### AAD B2C

While teaching authentication concepts is beyond the scope of this tutorial, learn how to [Create an Azure Active Directory B2C tenant](/azure/active-directory-b2c/tutorial-create-tenant), and then [Register a web app](/azure/active-directory-b2c/tutorial-register-applications) to consume it. For this shopping cart example app, register the resulting deployed Container Apps' URL in the B2C tenant. For more information, see [ASP.NET Core Blazor authentication and authorization](/aspnet/core/blazor/security).

> [!IMPORTANT]
> After the Container App is deployed, register the app's URL in the B2C tenant. In most production scenarios, the app's URL only needs registration once, as it shouldn't change.

To help visualize how the app is isolated within the Azure Container Apps environment, see the following diagram:

:::image type="content" source="media/azure-container-apps-http-ingress-diagram.png" lightbox="media/azure-container-apps-http-ingress-diagram.png" alt-text="Azure Container Apps HTTP ingress.":::

In the preceding diagram, all inbound traffic to the app funnels through a secured HTTP ingress. The Azure Container Apps environment contains an app instance, and the app instance contains an ASP.NET Core host, exposing the Blazor Server and Orleans app functionality.

## Deploy to Azure Container Apps

To deploy the app to Azure Container Apps, the repository uses GitHub Actions. Before this deployment can occur, a few Azure resources are needed, and the GitHub repository must be configured correctly.

[!INCLUDE [create-azure-resources](includes/deployment/create-azure-resources.md)]

[!INCLUDE [create-service-principal](includes/deployment/create-service-principal.md)]

[!INCLUDE [create-github-secret](includes/deployment/create-github-secret.md)]

### Prepare for Azure deployment

Package the app for deployment. In the `Orleans.ShoppingCart.Silos` project, a `Target` element is defined that runs after the `Publish` step. This target zips the publish directory into a _silo.zip_ file:

```xml
<Target Name="ZipPublishOutput" AfterTargets="Publish">
    <Delete Files="$(ProjectDir)\..\silo.zip" />
    <ZipDirectory SourceDirectory="$(PublishDir)" DestinationFile="$(ProjectDir)\..\silo.zip" />
</Target>
```

There are many ways to deploy a .NET app to Azure Container Apps. In this tutorial, use GitHub Actions, Azure Bicep, and the .NET and Azure CLIs. Consider the _./github/workflows/deploy.yml_ file in the root of the GitHub repository:

```yml
name: Deploy to Azure Container Apps

on:
  push:
    branches:
    - main

env:
  UNIQUE_APP_NAME: orleanscart
  SILO_IMAGE_NAME: orleanscart-silo
  AZURE_RESOURCE_GROUP_NAME: orleans-resourcegroup
  AZURE_RESOURCE_GROUP_LOCATION: eastus

jobs:
  build-and-deploy:
    runs-on: ubuntu-latest
    steps:
    - uses: actions/checkout@v3

    - name: Setup .NET 6.0
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: 6.0.x

    - name: .NET publish shopping cart app
      run: dotnet publish ./Silo/Orleans.ShoppingCart.Silo.csproj --configuration Release

    - name: Login to Azure
      uses: azure/login@v1
      with:
        creds: ${{ secrets.AZURE_CREDENTIALS }}

    - name: Flex ACR Bicep
      run: |
        az deployment group create \
          --resource-group ${{ env.AZURE_RESOURCE_GROUP_NAME }} \
          --template-file '.github/workflows/flex/acr.bicep' \
          --parameters location=${{ env.AZURE_RESOURCE_GROUP_LOCATION }}

    - name: Get ACR Login Server
      run: |
        ACR_NAME=$(az deployment group show -g ${{ env.AZURE_RESOURCE_GROUP_NAME }} -n acr \
        --query properties.outputs.acrName.value | tr -d '"')
        echo "ACR_NAME=$ACR_NAME" >> $GITHUB_ENV
        ACR_LOGIN_SERVER=$(az deployment group show -g ${{ env.AZURE_RESOURCE_GROUP_NAME }} -n acr \
        --query properties.outputs.acrLoginServer.value | tr -d '"')
        echo "ACR_LOGIN_SERVER=$ACR_LOGIN_SERVER" >> $GITHUB_ENV

    - name: Prepare Docker buildx
      uses: docker/setup-buildx-action@v1

    - name: Login to ACR
      run: |
        access_token=$(az account get-access-token --query accessToken -o tsv)
        refresh_token=$(curl https://${{ env.ACR_LOGIN_SERVER }}/oauth2/exchange -v \
        -d "grant_type=access_token&service=${{ env.ACR_LOGIN_SERVER }}&access_token=$access_token" | jq -r .refresh_token)
        # The null GUID 0000... tells the container registry that this is an ACR refresh token during the login flow
        docker login -u 00000000-0000-0000-0000-000000000000 \
        --password-stdin ${{ env.ACR_LOGIN_SERVER }} <<< "$refresh_token"

    - name: Build and push Silo image to registry
      uses: docker/build-push-action@v2
      with:
        push: true
        tags: ${{ env.ACR_LOGIN_SERVER }}/${{ env.SILO_IMAGE_NAME }}:${{ github.sha }}
        file: Silo/Dockerfile

    - name: Flex ACA Bicep
      run: |
        az deployment group create \
          --resource-group ${{ env.AZURE_RESOURCE_GROUP_NAME }} \
          --template-file '.github/workflows/flex/main.bicep' \
          --parameters location=${{ env.AZURE_RESOURCE_GROUP_LOCATION }} \
            appName=${{ env.UNIQUE_APP_NAME }} \
            acrName=${{ env.ACR_NAME }} \
            repositoryImage=${{ env.ACR_LOGIN_SERVER }}/${{ env.SILO_IMAGE_NAME }}:${{ github.sha }} \
          --debug

    - name: Get Container App URL
      run: |
        ACA_URL=$(az deployment group show -g ${{ env.AZURE_RESOURCE_GROUP_NAME }} \
        -n main --query properties.outputs.acaUrl.value | tr -d '"')
        echo $ACA_URL

    - name: Logout of Azure
      run: az logout
```

The preceding GitHub workflow does the following:

- Publishes the shopping cart app as a zip file using the [dotnet publish](../../core/tools/dotnet-publish.md) command.
- Logs in to Azure using credentials from the [Create a service principal](#create-a-service-principal) step.
- Evaluates the _acr.bicep_ file and starts a deployment group using [az deployment group create](/cli/azure/deployment/group#az-deployment-group-create).
- Gets the Azure Container Registry (ACR) login server from the deployment group.
- Logs in to ACR using the repository's `AZURE_CREDENTIALS` secret.
- Builds and publishes the silo image to the ACR.
- Evaluates the _main.bicep_ file and starts a deployment group using [az deployment group create](/cli/azure/deployment/group#az-deployment-group-create).
- Deploys the silo.
- Logs out of Azure.

The workflow triggers on a push to the `main` branch. For more information, see [GitHub Actions and .NET](../../devops/github-actions-overview.md).

> [!TIP]
> If you encounter issues when running the workflow, you might need to verify that the service principal has all the required provider namespaces registered. The following provider namespaces are required:
>
> - `Microsoft.App`
> - `Microsoft.ContainerRegistry`
> - `Microsoft.Insights`
> - `Microsoft.OperationalInsights`
> - `Microsoft.Storage`
>
> For more information, see [Resolve errors for resource provider registration](/azure/azure-resource-manager/troubleshooting/error-register-resource-provider?tabs=azure-cli).

Azure imposes naming restrictions and conventions for resources. Update the values in the _deploy.yml_ file for the following environment variables:

- `UNIQUE_APP_NAME`
- `SILO_IMAGE_NAME`
- `AZURE_RESOURCE_GROUP_NAME`
- `AZURE_RESOURCE_GROUP_LOCATION`

Set these values to your unique app name and your Azure resource group name and location.

For more information, see [Naming rules and restrictions for Azure resources](/azure/azure-resource-manager/management/resource-name-rules).

## Explore the Bicep templates

When the `az deployment group create` command runs, it evaluates a given _.bicep_ file reference. This file contains declarative information detailing the Azure resources to deploy. Think of this step as _provisioning_ all resources for deployment.

> [!IMPORTANT]
> If you're using Visual Studio Code, the Bicep authoring experience is improved when using the [Bicep Extension](https://marketplace.visualstudio.com/items?itemName=ms-azuretools.vscode-Bicep).

The first Bicep file evaluated is the _acr.bicep_ file. This file contains the Azure Container Registry (ACR) login server resource details:

```bicep
param location string = resourceGroup().location

resource acr 'Microsoft.ContainerRegistry/registries@2021-09-01' = {
  name: toLower('${uniqueString(resourceGroup().id)}acr')
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: true
  }
}

output acrLoginServer string = acr.properties.loginServer
output acrName string = acr.name
```

This Bicep file outputs the ACR login server and corresponding name. The next Bicep file encountered contains more than just a single `resource`. Consider the _main.bicep_ file, which consists primarily of delegating `module` definitions:

```bicep
param appName string
param acrName string
param repositoryImage string
param location string = resourceGroup().location

resource acr 'Microsoft.ContainerRegistry/registries@2021-09-01' existing = {
  name: acrName
}

module env 'environment.bicep' = {
  name: 'containerAppEnvironment'
  params: {
    location: location
    operationalInsightsName: '${appName}-logs'
    appInsightsName: '${appName}-insights'
  }
}

var envVars = [
  {
    name: 'APPINSIGHTS_INSTRUMENTATIONKEY'
    value: env.outputs.appInsightsInstrumentationKey
  }
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: env.outputs.appInsightsConnectionString
  }
  {
    name: 'ORLEANS_AZURE_STORAGE_CONNECTION_STRING'
    value: storageModule.outputs.connectionString
  }
  {
    name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
    value: 'true'
  }
]

module storageModule 'storage.bicep' = {
  name: 'orleansStorageModule'
  params: {
    name: '${appName}storage'
    location: location
  }
}

module siloModule 'container-app.bicep' = {
  name: 'orleansSiloModule'
  params: {
    appName: appName
    location: location
    containerAppEnvironmentId: env.outputs.id
    repositoryImage: repositoryImage
    registry: acr.properties.loginServer
    registryPassword: acr.listCredentials().passwords[0].value
    registryUsername: acr.listCredentials().username
    envVars: envVars
  }
}

output acaUrl string = siloModule.outputs.acaUrl
```

The preceding Bicep file does the following:

- References an `existing` ACR resource. For more information, see [Azure Bicep: Existing resources](/azure/azure-resource-manager/bicep/existing-resource).
- Defines a `module env` that delegates to the _environment.bicep_ definition file.
- Defines a `module storageModule` that delegates to the _storage.bicep_ definition file.
- Declares several shared `envVars` used by the silo module.
- Defines a `module siloModule` that delegates to the _container-app.bicep_ definition file.
- Outputs the ACA URL (potentially used to update an existing AAD B2C app registration's redirect URI).

The _main.bicep_ delegates to several other Bicep files. The first is the _environment.bicep_ file:

```bicep
param operationalInsightsName string
param appInsightsName string
param location string

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource logs 'Microsoft.OperationalInsights/workspaces@2021-06-01' = {
  name: operationalInsightsName
  location: location
  properties: {
    retentionInDays: 30
    features: {
      searchVersion: 1
    }
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource env 'Microsoft.App/managedEnvironments@2022-03-01' = {
  name: '${resourceGroup().name}env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

output id string = env.id
output appInsightsInstrumentationKey string = appInsights.properties.InstrumentationKey
output appInsightsConnectionString string = appInsights.properties.ConnectionString
```

This Bicep file defines the Azure Log Analytics and Application Insights resources. The `appInsights` resource is a `web` type, and the `logs` resource is a `PerGB2018` type. Both `appInsights` and `logs` resources are provisioned in the resource group's location. The `appInsights` resource links to the `logs` resource via the `WorkspaceResourceId` property. This Bicep file defines three outputs used later by the Container Apps `module`. Next, consider the _storage.bicep_ file:

```bicep
param name string
param location string

resource storage 'Microsoft.Storage/storageAccounts@2021-08-01' = {
  name: name
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
}

var key = listKeys(storage.name, storage.apiVersion).keys[0].value
var protocol = 'DefaultEndpointsProtocol=https'
var accountBits = 'AccountName=${storage.name};AccountKey=${key}'
var endpointSuffix = 'EndpointSuffix=${environment().suffixes.storage}'

output connectionString string = '${protocol};${accountBits};${endpointSuffix}'
```

The preceding Bicep file defines the following:

- Two parameters for the resource group name and the app name.
- The `resource storage` definition for the storage account.
- A single `output` constructing the connection string for the storage account.

The last Bicep file is the _container-app.bicep_ file:

```bicep
param appName string
param location string
param containerAppEnvironmentId string
param repositoryImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
param envVars array = []
param registry string
param registryUsername string
@secure()
param registryPassword string

resource containerApp 'Microsoft.App/containerApps@2022-03-01' = {
  name: appName
  location: location
  properties: {
    managedEnvironmentId: containerAppEnvironmentId
    configuration: {
      activeRevisionsMode: 'multiple'
      secrets: [
        {
          name: 'container-registry-password'
          value: registryPassword
        }
      ]
      registries: [
        {
          server: registry
          username: registryUsername
          passwordSecretRef: 'container-registry-password'
        }
      ]
      ingress: {
        external: true
        targetPort: 80
      }
    }
    template: {
      revisionSuffix: uniqueString(repositoryImage, appName)
      containers: [
        {
          image: repositoryImage
          name: appName
          env: envVars
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

output acaUrl string = containerApp.properties.configuration.ingress.fqdn
```

The aforementioned Visual Studio Code extension for Bicep includes a visualizer. All these Bicep files are visualized as follows:

:::image type="content" source="media/shopping-cart-container-app-Bicep-visualizer.png" alt-text="Orleans: Shopping cart sample app Bicep provisioning visualizer rendering." lightbox="media/shopping-cart-container-app-Bicep-visualizer.png":::

## Summary

As source code is updated and changes are `push`ed to the `main` branch of the repository, the _deploy.yml_ workflow runs. It provisions the Azure resources defined in the Bicep files and deploys the application. Revisions are automatically registered in the Azure Container Registry.

In addition to the visualizer from the Bicep extension, the Azure portal resource group page looks similar to the following example after provisioning and deploying the application:

:::image type="content" source="media/shopping-cart-aca-resources.png" alt-text="Azure portal: Orleans shopping cart sample app resources for Azure Container Apps." lightbox="media/shopping-cart-aca-resources.png":::

## See also

- [Orleans deployment overview](index.md)
- [GitHub Actions and .NET](../../devops/github-actions-overview.md)
- [Publish revisions with GitHub Actions in Azure Container Apps](/azure/container-apps/github-actions-cli)
