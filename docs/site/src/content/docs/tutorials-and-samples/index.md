---
title: Orleans tutorials and samples
description: Learn Orleans through tutorials, explanations, and repository-backed samples.
ms.date: 08/02/2026
ms.topic: sample
---

# Orleans tutorials and samples

Use this page to choose the right kind of learning material:

- **Quickstarts** lead you through a focused task.
- **Tutorials** teach a capability step by step.
- **Explanations** describe how a complete application is modeled.
- **Samples** are buildable applications which you can inspect and run.

## Start here

For the bare minimum application, start with [Orleans Hello World](hello-world.md). It hosts a silo and calls a grain from one process.

For a more typical project structure, [Build your first Orleans app](../quickstarts/build-your-first-orleans-app.md) using separate grain contract, implementation, silo, and external-client projects.

## Tutorials

| Tutorial | What it teaches |
| --- | --- |
| [Deploy an Orleans application to Azure Container Apps](production-application.md) | Run, deploy, observe, and verify a multi-process Orleans application. |
| [Test an Orleans application end to end](testing-walkthrough.md) | Progress from a first cluster test to reusable fixtures and topology changes. |
| [Build and recover a streaming application](streaming-walkthrough.md) | Follow events through a real provider and verify recovery from checkpoints. |
| [Custom grain storage](custom-grain-storage.md) | Implement and register an <xref:Orleans.Storage.IGrainStorage> provider. |
| [Deploy and scale on Azure](../quickstarts/deploy-scale-orleans-on-azure.md) | Deploy an Orleans app to Azure Container Apps and configure shared providers. |

## Explanations

| Article | What it explains |
| --- | --- |
| [Adventure game sample](adventure.md) | How rooms, players, and objects map to grains and ordinary values. |
| [Why Orleans](../benefits.md) | The benefits and tradeoffs of the virtual actor model. |
| [Orleans architecture design principles](../resources/orleans-architecture-principles-and-approach.md) | The design goals which shape Orleans APIs and runtime behavior. |

## Samples

The [`samples` directory](https://github.com/dotnet/orleans/tree/main/samples) contains the official Orleans samples. Its [sample catalog](https://github.com/dotnet/orleans/blob/main/samples/README.md) is generated from the repository manifest and is validated together with the sample projects.

### Fundamentals

| Sample | Demonstrates |
| --- | --- |
| [Hello World](https://github.com/dotnet/orleans/tree/main/samples/HelloWorld) | A single-project silo, grain contract, implementation, reference, and call. |
| [Adventure](https://github.com/dotnet/orleans/tree/main/samples/Adventure) | Domain modeling with grains and a standalone client. |
| [Chirper](https://github.com/dotnet/orleans/tree/main/samples/Chirper) | Persistence, observers, and reentrant grains. |
| [Simple Streaming](https://github.com/dotnet/orleans/tree/main/samples/Streaming/Simple) | Stream producers, consumers, and subscriptions. |

### Web and application integration

| Sample | Demonstrates |
| --- | --- |
| [Shopping Cart](https://github.com/dotnet/orleans/tree/main/samples/ShoppingCart) | Blazor, persistence, and a multi-project application. |
| [Blazor Server](https://github.com/dotnet/orleans/tree/main/samples/Blazor/BlazorServer) | A Blazor Server application backed by grains. |
| [Blazor WebAssembly](https://github.com/dotnet/orleans/tree/main/samples/Blazor/BlazorWasm) | A hosted WebAssembly client with an Orleans-backed server. |
| [GPS Tracker](https://github.com/dotnet/orleans/tree/main/samples/GPSTracker) | ASP.NET Core SignalR and IoT-style device updates. |
| [Presence Service](https://github.com/dotnet/orleans/tree/main/samples/Presence) | Observers and cooperating grains in a gaming scenario. |

### State, scheduling, and messaging

| Sample | Demonstrates |
| --- | --- |
| [Bank Account](https://github.com/dotnet/orleans/tree/main/samples/BankAccount) | ACID transactions across stateful grains. |
| [Journaled Todo List](https://github.com/dotnet/orleans/tree/main/samples/JournaledTodoList) | Event Sourcing with `JournaledGrain`, log-consistency providers, and Aspire. |
| [Journaling with Azure Blob JSON](https://github.com/dotnet/orleans/tree/main/samples/JournalingAzureBlobJson) | Experimental Journaling APIs with Azure Blob Storage. |
| [Chat Room](https://github.com/dotnet/orleans/tree/main/samples/ChatRoom) | A terminal chat application using Orleans Streams. |
| [Stocks](https://github.com/dotnet/orleans/tree/main/samples/Stocks) | Grain timers, HTTP calls, and temporary caching. |

The Azure Blob JSON sample uses the experimental `Microsoft.Orleans.Journaling` package. The Journaled Todo List uses the supported [Orleans Event Sourcing](../grains/event-sourcing/index.md) model. See the [Journaling sample guide](../grains/journaling/samples.md) to run the Azure Blob and Redis durable-state samples.

### Deployment and operations

| Sample | Demonstrates |
| --- | --- |
| [Azure Container Apps](https://github.com/dotnet/orleans/tree/main/samples/Deployment/AzureContainerApps) | A cluster, clients, dashboard, scaler, and Bicep deployment. |
| [Azure App Service](https://github.com/dotnet/orleans/tree/main/samples/Deployment/AzureAppService) | A multi-instance Orleans application on App Service. |
| [Authenticated Silo Connections](https://github.com/dotnet/orleans/tree/main/samples/AuthenticatedSiloConnections) | TLS and Microsoft Entra workload authentication for silo connections. |
| [Transport Layer Security](https://github.com/dotnet/orleans/tree/main/samples/TransportLayerSecurity) | Mutual TLS for Orleans network communication. |
| [Voting](https://github.com/dotnet/orleans/tree/main/samples/Voting) | Kubernetes-oriented deployment and the Orleans Dashboard. |

The repository catalog also includes F#, Visual Basic, games, custom stream adapters, and other focused examples.

## Validate samples locally

After cloning the Orleans repository, run the sample validation script from the repository root:

```powershell
pwsh ./samples/Validate-Samples.ps1
```

The script validates the gallery manifest and builds every project in `samples/Samples.slnx`. Cloud credentials are needed only to run samples which connect to cloud services, not to compile them.
