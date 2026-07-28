---
title: Deploy and scale an Orleans app on Azure
description: Host and scale an Orleans app on Azure Container Apps with Azure Container Registry and Azure Table Storage or Azure Cosmos DB for NoSQL.
ms.topic: how-to
ms.devlang: csharp
ms.date: 07/03/2024
zone_pivot_groups: orleans-persistence-option
# CustomerIntent: As a developer, I want to host my Orleans application in Azure so that I can take advantage of the scaling capabilities for the database and application services.
---

# Deploy and scale an Orleans app on Azure

In this quickstart, you deploy and scale an Orleans URL shortener app on Azure Container Apps. The app allows users to submit a full URL to the app, which returns a shortened version they can share with others to direct them to the original site. Orleans and Azure provide the scalability features necessary to host high traffic apps like URL shorteners. Orleans is also compatible with any other hosting service that supports .NET.

At the end of this quickstart, you have a scalable app running in Azure to provide URL shortener functionality. Along the way you learn to:

- Pull and Azure Developer CLI template
- Deploy an Orleans app to Azure
- Scale the app to multiple instances

## Prerequisites

- An Azure account with an active subscription. [Create an account for free](https://azure.microsoft.com/pricing/purchase-options/azure-account?cid=msft_learn).
- [Azure Developer CLI](/azure/developer/azure-developer-cli/install-azd)
- [.NET 8](https://dotnet.microsoft.com/download)
- [Docker](https://www.docker.com/)

## Get and deploy the sample application

The sample application is available as an Azure Developer CLI template. Through this quickstart: you pull the template application, deploy the template and sample code to Azure, change the template to implement your preferred persistence grain, deploy the necessary resources, and then deploy the final application.

1. Open a terminal in an empty directory.

1. Authenticate to the Azure Developer CLI using `azd auth login`. Follow the steps specified by the tool to authenticate to the CLI using your preferred Azure credentials.

   ```azuredeveloper
   azd auth login
   ```

1. Get the sample application using the AZD template `orleans-url-shortener` and the `azd init` command.

   ```azuredeveloper
   azd init --template orleans-url-shortener
   ```

1. During initialization, configure a unique environment name.

   > [!TIP]
   > The environment name will also be used as the target resource group name. For this quickstart, consider using `msdocs-orleans-url-shortener`.

1. Deploy the Azure Cosmos DB for NoSQL account using `azd up`. The Bicep templates also deploy a sample web application.

   ```azuredeveloper
   azd up
   ```

1. During the provisioning process, select your subscription and desired location. Wait for the provisioning and deployment process to complete. The process can take **approximately five minutes**.

1. Once the provisioning of your Azure resources is done, a URL to the running web application is included in the output.

   ```output
   Deploying services (azd deploy)

     (✓) Done: Deploying service web
   - Endpoint: <https://[container-app-sub-domain].azurecontainerapps.io>

   SUCCESS: Your application was provisioned and deployed to Azure in 5 minutes 0 seconds.
   ```

1. Use the URL in the console to navigate to your web application in the browser.

   :::image type="content" source="media/deploy-scale-Orleans-on-azure/web-application.png" alt-text="Screenshot of the running URL shortener web application.":::

1. In the browser address bar, test the `shorten` endpoint by adding a URL path such as `/shorten?url=https://www.microsoft.com`. The page should reload and provide a new URL with a shortened path at the end. Copy the new URL to your clipboard.

   ```output
   {
     "original": "https://www.microsoft.com",
     "shortened": "http://<container-app-name>.<deployment-name>.<region>.azurecontainerapps.io:<port>/go/<generated-id>"
   }
   ```

1. Paste the shortened URL into the address bar and press enter. The page should reload and redirect you to the URL you specified.

## Deploy extra services

The original deployment only deployed the minimal services necessary to host the URL shortener app. To use an Azure data service for grain persistence, you must first configure the template to deploy your preferred service.

::: zone pivot="azure-storage"

1. Using the terminal, run `azd env set` to configure the `DEPLOY_AZURE_TABLE_STORAGE` environment variable to enable deployment of Azure Cosmos DB for NoSQL.

   ```azuredeveloper
   azd env set DEPLOY_AZURE_TABLE_STORAGE true
   ```

::: zone-end

::: zone pivot="azure-cosmos-db-nosql"

1. Using the terminal, run `azd env set` to configure the `DEPLOY_AZURE_COSMOS_DB_NOSQL` environment variable to enable deployment of Azure Cosmos DB for NoSQL.

   ```azuredeveloper
   azd env set DEPLOY_AZURE_COSMOS_DB_NOSQL true
   ```

::: zone-end

1. Run `azd provision` to redeploy your application architecture with the new configuration. Wait for the provisioning process to complete. The process can take **approximately two minutes**.

   ```azuredeveloper
   azd provision
   ```

   > [!TIP]
   > Alternatively, you can run `azd up` again which will both provision your architecture and redeploy your application.

## Install NuGet packages

Prior to using the grain, you must install the corresponding `Microsoft.Orleans.Clustering.*` and `Microsoft.Orleans.Persistence.*` NuGet packages. These services use role-based access control for passwordless authentication, so you must also import the `Azure.Identity` NuGet package.

::: zone pivot="azure-storage"

1. Change your current working directory to _./src/web/_.

   ```bash
   cd ./src/web
   ```

1. Import the `Azure.Identity` package from NuGet:

   ```dotnetcli
   dotnet add package Azure.Identity --version 1.*
   ```

1. Import the `Microsoft.Orleans.Clustering.AzureStorage` and `Microsoft.Orleans.Persistence.AzureStorage` packages.

   | Feature         | NuGet package                                |
   |-----------------|----------------------------------------------|
   | **Clustering**  | `Microsoft.Orleans.Clustering.AzureStorage`  |
   | **Persistence** | `Microsoft.Orleans.Persistence.AzureStorage` |

   ```dotnetcli
   dotnet add package Microsoft.Orleans.Clustering.AzureStorage --version 8.*
   dotnet add package Microsoft.Orleans.Persistence.AzureStorage --version 8.*
   ```

::: zone-end

::: zone pivot="azure-cosmos-db-nosql"

1. Import the `Azure.Identity` package from NuGet:

   ```dotnetcli
   dotnet add package Azure.Identity --version 1.*
   ```

1. Import the `Microsoft.Orleans.Clustering.Cosmos` and `Microsoft.Orleans.Persistence.Cosmos` packages.

   | Feature         | NuGet package                          |
   |-----------------|----------------------------------------|
   | **Clustering**  | `Microsoft.Orleans.Clustering.Cosmos`  |
   | **Persistence** | `Microsoft.Orleans.Persistence.Cosmos` |

   ```dotnetcli
   dotnet add package Microsoft.Orleans.Clustering.Cosmos --version 8.*
   dotnet add package Microsoft.Orleans.Persistence.Cosmos --version 8.*
   ```

::: zone-end

## Configure and redeploy the sample app

The sample app is currently configured to create a localhost cluster and persist grains in-memory. When hosted in Azure, Orleans can be configured to use more scalable, centralized state using a data service in Azure.

1. Add the following `using` directives:

   ```csharp
   using Azure.Identity;
   using Orleans.Configuration;
   ```

1. Find and remove the current `builder` configuration code in the _src/web/Program.cs_ file.

   ```csharp
   builder.Host.UseOrleans(static siloBuilder =>
   {
       siloBuilder
           .UseLocalhostClustering()
           .AddMemoryGrainStorage("urls");
   });
   ```

::: zone pivot="azure-storage"

1. Replace the `builder` configuration with the example here, which implements these key concepts:

   - A conditional environment check is added to ensure the app runs properly in both local development and Azure hosted scenarios.
   - The `UseAzureStorageClustering` method configures the Orleans cluster to use Azure Table Storage and authenticates using the <xref:Azure.Identity.DefaultAzureCredential> class.
   - Use the `Configure` method to assign IDs for the Orleans cluster.
   - The `ClusterID` is a unique ID for the cluster that allows clients and silos to talk to one another.
   - The `ClusterID` can change across deployments.
   - The `ServiceID` is a unique ID for the application that is used internally by Orleans and should remain consistent across deployments.

    ```csharp
    if (builder.Environment.IsDevelopment())
    {
        builder.Host.UseOrleans(static siloBuilder =>
        {
            siloBuilder
                .UseLocalhostClustering()
                .AddMemoryGrainStorage("urls");
        });
    }
    else
    {
        builder.Host.UseOrleans(siloBuilder =>
        {
            var endpoint = new Uri(builder.Configuration["AZURE_TABLE_STORAGE_ENDPOINT"]!);
            var credential = new DefaultAzureCredential();

            siloBuilder
                .UseAzureStorageClustering(options =>
                {
                    options.ConfigureTableServiceClient(endpoint, credential);
                })
                .AddAzureTableGrainStorage(name: "urls", options =>
                {
                    options.ConfigureTableServiceClient(endpoint, credential);
                })
                .Configure<ClusterOptions>(options =>
                {
                    options.ClusterId = "url-shortener";
                    options.ServiceId = "urls";
                });
        });
    }
    ```

::: zone-end

::: zone pivot="azure-cosmos-db-nosql"

1. Replace the `builder` configuration with the example here, which implements these key concepts:

   - A conditional environment check is added to ensure the app runs properly in both local development and Azure hosted scenarios.
   - The `UseCosmosClustering` method configures the Orleans cluster to use Azure Cosmos DB for NoSQL and authenticates using the <xref:Azure.Identity.DefaultAzureCredential> class.
   - Use the `Configure` method to assign IDs for the Orleans cluster.
   - The `ClusterID` is a unique ID for the cluster that allows clients and silos to talk to one another.
   - The `ClusterID` can change across deployments.
   - The `ServiceID` is a unique ID for the application that is used internally by Orleans and should remain consistent across deployments.

   ```csharp
   if (builder.Environment.IsDevelopment())
   {
       builder.Host.UseOrleans(static siloBuilder =>
       {
           siloBuilder
               .UseLocalhostClustering()
               .AddMemoryGrainStorage("urls");
       });
   }
   else
   {
       builder.Host.UseOrleans(siloBuilder =>
       {
           var endpoint = builder.Configuration["AZURE_COSMOS_DB_NOSQL_ENDPOINT"]!;
           var credential = new DefaultAzureCredential();

           siloBuilder
               .UseCosmosClustering(options =>
               {
                   options.ConfigureCosmosClient(endpoint, credential);
               })
               .AddCosmosGrainStorage(name: "urls", options =>
               {
                   options.ConfigureCosmosClient(endpoint, credential);
               })
               .Configure<ClusterOptions>(options =>
               {
                   options.ClusterId = "url-shortener";
                   options.ServiceId = "urls";
               });
       });
   }
   ```

::: zone-end

1. Run `azd deploy` to redeploy your application code as a Docker container. Wait for the deployment process to complete. The process can take **approximately one minute**.

   ```azuredeveloper
   azd deploy
   ```

   > [!TIP]
   > Alternatively, you can run `azd up` again which will both provision your architecture and redeploy your application.

## Verify the app's behavior

Validate that your updated code works by using the deployed application again and checking to see where it stores data.

1. In the browser address bar, test the `shorten` endpoint again by adding a URL path such as `/shorten?url=https://learn.microsoft.com/dotnet/orleans`. The page should reload and provide a new URL with a shortened path at the end. Copy the new URL to your clipboard.

   ```output
   {
     "original": "https://learn.microsoft.com/dotnet/orleans",
    "shortened": "http://<container-app-name>.<deployment-name>.<region>.azurecontainerapps.io:<port>/go/<generated-id>"
   }
   ```

1. Paste the shortened URL into the address bar and press enter. The page should reload and redirect you to the URL you specified.

Optionally, you can verify that the cluster and state data is stored as expected in the storage account you created.

1. In the Azure portal, navigate to the resource group that was deployed in this quickstart.

   > [!IMPORTANT]
   > The environment name specified earlier in this quickstart is also the target resource group name.

::: zone pivot="azure-storage"

1. Navigate to the overview page of the Azure Storage account.

1. Within the navigation, select **Storage browser**.

1. Expand the **Tables** navigation item to discover two tables created by Orleans:

   - **OrleansGrainState**: This table stores the persistent state grain data used by the application to handle the URL redirects.
   - **OrleansSiloInstances**: This table tracks essential silo data for the Orleans cluster.

1. Select the **OrleansGrainState** table. The table holds a row entry for every URL redirect persisted by the app during your testing.

   :::image type="content" source="media/deploy-scale-Orleans-on-azure/storage-table-entities.png" alt-text="A screenshot showing Orleans data in Azure Table Storage.":::

::: zone-end

::: zone pivot="azure-cosmos-db-nosql"

1. Navigate to the overview page of the Azure Cosmos DB for NoSQL account.

1. Within the navigation,select **Data Explorer**.

1. Observe the following containers you created earlier in this guide:

   - **OrleansStorage**: This table stores the persistent state grain data used by the application to handle the URL redirects.

   - **OrleansCluster**: This table tracks essential silo data for the Orleans cluster.

::: zone-end

## Scale the app

Orleans is designed for distributed applications. Even an app as simple as the URL shortener can benefit from the scalability of Orleans. You can scale and test your app across multiple instances using the following steps:

1. Navigate back to the resource group that was deployed in this quickstart.

1. Navigate to the overview page of the Azure Container Apps app.

1. Within the navigation, select **Scale**.

1. Select **Edit and deploy**, and then switch to the **Scale** tab.

1. Use the slider control to set the min and max replica values to 4. This value ensures the app is running on multiple instances.

1. Select **Create** to deploy the new revision.

   :::image type="content" source="media/deploy-scale-Orleans-on-azure/scale-containers.png" alt-text="A screenshot showing how to scale the Azure Container Apps app.":::

1. After the deployment is finished, repeat the testing steps from the previous section. The app continues to work as expected across several instances and can now handle a higher number of requests.

## Related content

::: zone pivot="azure-storage"

- [Azure Table Storage](/azure/storage/tables)
- [Azure Storage grain persistence](../grains/grain-persistence/azure-storage.md)

::: zone-end

::: zone pivot="azure-cosmos-db-nosql"

- [Azure Cosmos DB for NoSQL](/azure/cosmos-db/nosql)
- [Azure Cosmos DB grain persistence](../grains/grain-persistence/azure-cosmos-db.md)

::: zone-end

## Next step

> [!div class="nextstepaction"]
> [Grain persistence](../grains/grain-persistence/index.md)
