# Orleans Samples

This directory is the canonical home of the official Orleans samples.

<!-- Generated from gallery.json by Update-Readme.ps1. -->

Samples imported from other Microsoft repositories retain their original source repository in the index and in `gallery.json`. Their source licenses are preserved alongside the imported content.

## Build and validate

From the repository root, run:

```powershell
pwsh ./samples/Validate-Samples.ps1
```

The command checks the gallery manifest and builds every project in `Samples.slnx`. External cloud services are required only when running samples which use them, not when compiling.

## Featured samples

| Sample | Description | Original source |
| --- | --- | --- |
| [Adventure](Adventure) | A text adventure game demonstrating grains, external clients, and application modeling. | [dotnet/samples](https://github.com/dotnet/samples) |
| [Chirper](Chirper) | A social network sample using persistence, observers, and reentrant grains. | [dotnet/samples](https://github.com/dotnet/samples) |
| [GPS Tracker](GPSTracker) | An IoT location tracker integrating Orleans with ASP.NET Core SignalR. | [dotnet/samples](https://github.com/dotnet/samples) |
| [Hello World](HelloWorld) | The smallest complete Orleans application for defining and calling a grain. | [dotnet/samples](https://github.com/dotnet/samples) |
| [Journaled Todo List](JournaledTodoList) | An Aspire-hosted Web application demonstrating durable journaled grain state. | [dotnet/samples](https://github.com/dotnet/samples) |
| [Presence Service](Presence) | A gaming presence service using observers and multiple cooperating grains. | [dotnet/samples](https://github.com/dotnet/samples) |
| [Shopping Cart](ShoppingCart) | A Blazor shopping cart using Orleans persistence and Azure Storage. | [dotnet/samples](https://github.com/dotnet/samples) |
| [Azure App Service Deployment](Deployment/AzureAppService) | A Windows and Linux Azure App Service deployment with managed identity, private silo networking, health checks, Easy Auth, Bicep, and OIDC. | [dotnet/orleans](https://github.com/dotnet/orleans) |
| [Azure Container Apps Deployment](Deployment/AzureContainerApps) | A managed-identity Orleans cluster with clients, dashboard, scaler, and Bicep deployment for Azure Container Apps. | [Azure-Samples/Orleans-Cluster-on-Azure-Container-Apps](https://github.com/Azure-Samples/Orleans-Cluster-on-Azure-Container-Apps) |

## All samples

| Sample | Description | Languages | Tags | Original source |
| --- | --- | --- | --- | --- |
| [Adventure](Adventure) | A text adventure game demonstrating grains, external clients, and application modeling. | C# | games, clients, grains | [dotnet/samples](https://github.com/dotnet/samples) |
| [AWS Kinesis and DynamoDB](AWS/KinesisDynamoDB) | An AWS-hosted Orleans application using DynamoDB for clustering, persistence, reminders, and Kinesis checkpoints. | C# | aws, kinesis, dynamodb, streaming | [dotnet/orleans](https://github.com/dotnet/orleans) |
| [Authenticated Silo Connections](AuthenticatedSiloConnections) | A silo cluster using TLS and Microsoft Entra workload authentication for silo connections. | C# | security, tls, entra, networking | [dotnet/orleans](https://github.com/dotnet/orleans) |
| [Bank Account](BankAccount) | A bank transfer simulation demonstrating ACID transactions across stateful grains. | C# | transactions, persistence | [dotnet/samples](https://github.com/dotnet/samples) |
| [Basic Clustering](BasicClustering) | A minimal Aspire-hosted Orleans cluster with two silo replicas and Redis membership. | C# | clustering, aspire, redis, getting-started | [dotnet/orleans](https://github.com/dotnet/orleans) |
| [Blazor Server](Blazor/BlazorServer) | An interactive Blazor Server application backed by Orleans grains. | C#, Razor | blazor, aspnet-core, web | [dotnet/samples](https://github.com/dotnet/samples) |
| [Blazor WebAssembly](Blazor/BlazorWasm) | A hosted Blazor WebAssembly application with an Orleans-backed server. | C#, Razor | blazor, webassembly, web | [dotnet/samples](https://github.com/dotnet/samples) |
| [Chat Room](ChatRoom) | A terminal chat application demonstrating Orleans Streams. | C# | streaming, client, terminal | [dotnet/samples](https://github.com/dotnet/samples) |
| [Chirper](Chirper) | A social network sample using persistence, observers, and reentrant grains. | C# | observers, persistence, reentrancy | [dotnet/samples](https://github.com/dotnet/samples) |
| [Custom grain-call return type](CustomGrainCallReturnType) | An awaitable GrainCall<T> extension demonstrating generated request bases, proxy adaptation, and failure propagation. | C# | serialization, code-generation, grains | [dotnet/orleans](https://github.com/dotnet/orleans) |
| [F# Hello World](FSharpHelloWorld) | A minimal Orleans application with grains implemented in F#. | F#, C# | getting-started, fsharp | [dotnet/samples](https://github.com/dotnet/samples) |
| [GPS Tracker](GPSTracker) | An IoT location tracker integrating Orleans with ASP.NET Core SignalR. | C#, JavaScript | iot, signalr, web | [dotnet/samples](https://github.com/dotnet/samples) |
| [Google Cloud Firestore](GoogleFirestore) | An Orleans application using Firestore for clustering, grain directories, persistence, and reminders. | C# | google-cloud, firestore, clustering, persistence, reminders | [dotnet/orleans](https://github.com/dotnet/orleans) |
| [Hello World](HelloWorld) | The smallest complete Orleans application for defining and calling a grain. | C# | getting-started, grains | [dotnet/samples](https://github.com/dotnet/samples) |
| [Journaled Todo List](JournaledTodoList) | An Aspire-hosted Web application demonstrating durable journaled grain state. | C#, Razor | journaling, aspire, web | [dotnet/samples](https://github.com/dotnet/samples) |
| [Journaling with Azure Blob JSON](JournalingAzureBlobJson) | An Aspire sample using Orleans journaling with JSON events stored in Azure Blob Storage. | C# | journaling, azure-storage, aspire | [dotnet/orleans](https://github.com/dotnet/orleans) |
| [Presence Service](Presence) | A gaming presence service using observers and multiple cooperating grains. | C# | gaming, observers, services | [dotnet/samples](https://github.com/dotnet/samples) |
| [Shopping Cart](ShoppingCart) | A Blazor shopping cart using Orleans persistence and Azure Storage. | C#, Razor | blazor, persistence, azure-storage | [dotnet/samples](https://github.com/dotnet/samples) |
| [Stocks](Stocks) | A stock price application using grain timers, HTTP calls, and temporary caching. | C#, Razor | timers, http, caching | [dotnet/samples](https://github.com/dotnet/samples) |
| [Streaming Custom Data Adapter](Streaming/CustomDataAdapter) | An Event Hubs streaming sample consuming data from a non-Orleans publisher. | C# | streaming, event-hubs, azure | [dotnet/samples](https://github.com/dotnet/samples) |
| [Simple Streaming](Streaming/Simple) | A compact producer and consumer example using Orleans Streams. | C# | streaming, getting-started | [dotnet/samples](https://github.com/dotnet/samples) |
| [Tic Tac Toe](TicTacToe) | A Web-based game demonstrating lobbies and ASP.NET Core integration. | C#, JavaScript | games, aspnet-core, web | [dotnet/samples](https://github.com/dotnet/samples) |
| [Transport Layer Security](TransportLayerSecurity) | A Hello World cluster configured with mutual TLS for all network communication. | C# | security, tls, networking | [dotnet/samples](https://github.com/dotnet/samples) |
| [Visual Basic Hello World](VBHelloWorld) | A minimal Orleans application with grain contracts and implementations in Visual Basic. | Visual Basic, C# | getting-started, visual-basic | [dotnet/samples](https://github.com/dotnet/samples) |
| [Voting](Voting) | A Kubernetes-oriented voting Web application with the Orleans Dashboard. | C#, Razor | kubernetes, dashboard, web | [dotnet/samples](https://github.com/dotnet/samples) |
| [Azure App Service Deployment](Deployment/AzureAppService) | A Windows and Linux Azure App Service deployment with managed identity, private silo networking, health checks, Easy Auth, Bicep, and OIDC. | C#, Razor, Bicep, YAML | deployment, azure, app-service, windows, linux | [dotnet/orleans](https://github.com/dotnet/orleans) |
| [Azure Container Apps Deployment](Deployment/AzureContainerApps) | A managed-identity Orleans cluster with clients, dashboard, scaler, and Bicep deployment for Azure Container Apps. | C#, Bicep, YAML | deployment, azure, container-apps, managed-identity, networking | [Azure-Samples/Orleans-Cluster-on-Azure-Container-Apps](https://github.com/Azure-Samples/Orleans-Cluster-on-Azure-Container-Apps) |
