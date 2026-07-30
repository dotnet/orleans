---
title: 'Quickstart: Build your first Orleans app with ASP.NET Core'
description: Learn how to use Orleans to build a scalable, distributed ASP.NET Core application
ms.date: 08/14/2024
ms.topic: quickstart
ms.devlang: csharp
zone_pivot_groups: orleans-version
---

# Quickstart: Build your first Orleans app with ASP.NET Core

In this quickstart, you use Orleans and ASP.NET Core 8.0 Minimal APIs to build a URL shortener app. Users submit a full URL to the app's `/shorten` endpoint and get a shortened version to share with others, who are redirected to the original site. The app uses Orleans grains and silos to manage state in a distributed manner to allow for scalability and resiliency. These features are critical when developing apps for distributed cloud hosting services like Azure Container Apps and platforms like Kubernetes.

At the end of the quickstart, you have an app that creates and handles redirects using short, friendly URLs. You learn how to:

- Add Orleans to an ASP.NET Core app
- Work with grains and silos
- Configure state management
- Integrate Orleans with API endpoints

## Prerequisites

# [Visual Studio](#tab/visual-studio)

- [.NET 8.0 SDK or later](https://dotnet.microsoft.com/download/dotnet)
- [Visual Studio 2022 or later](https://visualstudio.microsoft.com/) with the **ASP.NET and web development** workload

# [Visual Studio Code](#tab/visual-studio-code)

[!INCLUDE [Prerequisites](../../../includes/prerequisites-basic.md)]

---

## Create the app

# [Visual Studio](#tab/visual-studio)

1. Start Visual Studio and select **Create a new project**.

1. On the **Create a new project** dialog, select **ASP.NET Core Web API**, and then select **Next**.

1. On the **Configure your new project** dialog, enter `OrleansURLShortener` for **Project name**, and then select **Next**.

1. On the **Additional information** dialog, select **.NET 8.0 (Long Term Support)** and uncheck **Use controllers**, and then select **Create**.

# [Visual Studio Code](#tab/visual-studio-code)

1. Inside Visual Studio Code, open the [integrated terminal](https://code.visualstudio.com/docs/editor/integrated-terminal).

1. Change to the directory (`cd`) that will contain the project.
1. Run the following commands:

   ```dotnetcli
   dotnet new webapi -o OrleansURLShortener
   code -r OrleansURLShortener
   ```

   The `dotnet new` command creates a new Minimal API project in the *OrleansURLShortener* folder. The `code` command opens the *OrleansURLShortener* folder in the current instance of Visual Studio Code.

   Visual Studio Code displays a dialog box that asks **Do you trust the authors of the files in this folder**.
   Select:

   - The checkbox **trust the authors of all files in the parent folder**
   - **Yes, I trust the authors** (because dotnet generated the files).

---

## Add Orleans to the project

Orleans is available through a collection of NuGet packages, each of which provides access to various features. For this quickstart, add the [Microsoft.Orleans.Server](https://www.nuget.org/packages/Microsoft.Orleans.Server) NuGet package to the app:

# [Visual Studio](#tab/visual-studio)

- Right-click on the **OrleansURLShortener** project node in the solution explorer and select **Manage NuGet Packages**.
- In the package manager window, search for *Orleans*.
- Choose the **Microsoft.Orleans.Server** package from the search results and then select **Install**.

# [Visual Studio Code](#tab/visual-studio-code)

In the Visual Studio Code terminal, run the following command:

```dotnetcli
dotnet add package Microsoft.Orleans.Server
```

Or, in .NET 10+:

```dotnetcli
dotnet package add Microsoft.Orleans.Server
```

---

Open the _Program.cs_ file and replace the existing content with the following code:

```csharp
using Orleans.Runtime;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();
```

## Configure the silos

[Silos](../overview.md#what-are-silos) are a core building block of Orleans responsible for storing and managing grains. A silo can contain one or more grains; a group of silos is known as a cluster. The cluster coordinates work between silos, allowing communication with grains as though they were all available in a single process.

At the top of the _Program.cs_ file, refactor the code to use Orleans. The following code uses a <xref:Orleans.Hosting.ISiloBuilder> class to create a localhost cluster with a silo that can store grains. The <xref:Orleans.Hosting.ISiloBuilder> also uses the <xref:Orleans.Hosting.MemoryGrainStorageSiloBuilderExtensions.AddMemoryGrainStorage*> to configure the Orleans silos to persist grains in memory. This scenario uses local resources for development, but a production app can be configured to use highly scalable clusters and storage using services like Azure Blob Storage.

:::code source="snippets/url-shortener/orleansurlshortener/Program.cs" id="configuration" :::

## Create the URL shortener grain

[Grains](../overview.md) are the most essential primitives and building blocks of Orleans applications. A grain is a class that inherits from the <xref:Orleans.Grain> base class, which manages various internal behaviors and integration points with Orleans. Grains should also implement one of the following interfaces to define their grain key identifier. Each of these interfaces defines a similar contract, but marks your class with a different data type for the identifier that Orleans uses to track the grain, such as a string or integer.

- <xref:Orleans.IGrainWithGuidKey>
- <xref:Orleans.IGrainWithIntegerKey>
- <xref:Orleans.IGrainWithStringKey>
- <xref:Orleans.IGrainWithGuidCompoundKey>
- <xref:Orleans.IGrainWithIntegerCompoundKey>

For this quickstart, you use the <xref:Orleans.IGrainWithStringKey>, since strings are a logical choice for working with URL values and short codes.

Orleans grains can also use a custom interface to define their methods and properties. The URL shortener grain interface should define two methods:

- A `SetUrl` method to persist the original and their corresponding shortened URLs.
- A `GetUrl` method to retrieve the original URL given the shortened URL.

1. Append the following interface definition to the bottom of the _Program.cs_ file.

    :::code source="snippets/url-shortener/orleansurlshortener/Program.cs" id="graininterface":::

1. Create a `UrlShortenerGrain` class using the following code. This class inherits from the <xref:Orleans.Grain> class provided by Orleans and implements the `IUrlShortenerGrain` interface you created. The class also uses the <xref:Orleans.Runtime.IPersistentState`1> interface of Orleans to manage reading and writing state values for the URLs to the configured silo storage.

    :::code source="snippets/url-shortener/orleansurlshortener/Program.cs" id="grain":::

## Create the endpoints

Next, create two endpoints to utilize the Orleans grain and silo configurations:

- A `/shorten` endpoint to handle creating and storing a shortened version of the URL. The original, full URL is provided as a query string parameter named `url`, and the shortened URL is returned to the user for later use.
- A `/go/{shortenedRouteSegment:required}` endpoint to handle redirecting users to the original URL using the shortened URL that is supplied as a parameter.

Inject the <xref:Orleans.IGrainFactory> interface into both endpoints. Grain Factories enable you to retrieve and manage references to individual grains that are stored in silos. Append the following code to the _Program.cs_ file before the `app.Run()` method call:

:::code source="snippets/url-shortener/orleansurlshortener/Program.cs" id="endpoints":::

## Test the completed app

The core functionality of the app is now complete and ready to be tested. The final app code should match the following example:

:::code source="snippets/url-shortener/orleansurlshortener/Program.cs":::

Test the application in the browser using the following steps:

# [Visual Studio](#tab/visual-studio)

1. Start the app using the run button at the top of Visual Studio. The app should launch in the browser and display the familiar `Hello world!` text.

1. In the browser address bar, test the `shorten` endpoint by entering a URL path such as `{localhost}/shorten?url=https://learn.microsoft.com`. The page should reload and provide a shortened URL. Copy the shortened URL to your clipboard.

    :::image type="content" source="../media/url-shortener.png" alt-text="A screenshot showing the result of the URL shortener launched from Visual Studio.":::

1. Paste the shortened URL into the address bar and press enter. The page should reload and redirect you to [https://learn.microsoft.com](https://learn.microsoft.com).

# [Visual Studio Code](#tab/visual-studio-code)

1. Inside the Visual Studio Code terminal, run the `dotnet run` command again to launch the app. The app should launch in the browser and display the familiar `Hello world!` text.

    ```dotnetcli
    dotnet run
    ```

1. In the browser address bar, test the `shorten` endpoint by entering a URL path such as `{localhost}/shorten?url=https://learn.microsoft.com`. The page should reload and provide a shortened URL. Copy the shortened URL to your clipboard.

    :::image type="content" source="../media/url-shortener.png" alt-text="A screenshot showing the result of the URL shortener launched from Visual Studio Code.":::

1. Paste the shortened URL into the address bar and press enter. The page should reload and redirect you to [https://learn.microsoft.com](https://learn.microsoft.com).

---

:::zone target="docs" pivot="orleans-8-0,orleans-9-0,orleans-10-0"

## Next steps: Production-ready Orleans with Aspire

The quickstart above uses in-memory storage and localhost clustering, which works well for development but isn't suitable for production deployments. When you're ready to build production-ready Orleans applications, **Aspire** provides a streamlined approach with:

- **Resource management**: Easily add Redis, Azure Storage, or SQL databases as backing stores for clustering, grain storage, and reminders
- **Service discovery**: Automatic configuration of Orleans silos and clients without manual endpoint configuration
- **Observability**: Built-in OpenTelemetry integration for distributed tracing, metrics, and logging
- **Health checks**: Automatic health check endpoints for your Orleans cluster
- **Multi-replica support**: Scale your Orleans silos horizontally with `WithReplicas()`

### Quick example with Aspire

Here's how the URL shortener app would look with Aspire and Redis:

**AppHost project (Program.cs):**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Add Redis for clustering and grain storage
var redis = builder.AddRedis("redis");

// Configure Orleans to use Redis
var orleans = builder.AddOrleans("cluster")
    .WithClustering(redis)
    .WithGrainStorage("urls", redis);

// Add the Orleans silo project
builder.AddProject<Projects.OrleansURLShortener>("silo")
    .WithReference(orleans)
    .WithReference(redis);

builder.Build().Run();
```

**Silo project (Program.cs):**

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddKeyedRedisClient("redis");
builder.UseOrleans();

var app = builder.Build();
// ... rest of app configuration
```

With Aspire, Orleans automatically picks up the cluster configuration from environment variables injected by the AppHost, eliminating the need for manual configuration code.

> [!div class="nextstepaction"]
> [Learn more about Orleans and Aspire integration](../host/aspire-integration.md)

:::zone-end

:::zone target="docs" pivot="orleans-3-x,orleans-7-0"

## Next steps

> [!div class="nextstepaction"]
> [Tutorial: Create a minimal Orleans application](../tutorials-and-samples/tutorial-1.md)

:::zone-end
