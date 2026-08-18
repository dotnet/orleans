---
title: 'Tutorial: Build your first Orleans app'
description: Build a multi-project Orleans application with a silo and external client.
ms.date: 08/18/2026
ms.topic: tutorial
ms.devlang: csharp
---

# Tutorial: Build your first Orleans app

In this tutorial, you build a small Orleans application using project boundaries typical of larger applications. Grain contracts, grain implementations, the silo, and an external client are separate projects with distinct dependencies and deployment roles.

You learn how to:

- Define and implement a grain.
- Host grains in a silo.
- Connect an external client to a silo.
- Obtain a grain reference and call it.
- Structure project references so that clients depend on contracts, not implementations.

The example uses localhost clustering and omits production concerns such as durable storage, authentication, observability, and application-level retry policies.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- An editor such as [Visual Studio](https://visualstudio.microsoft.com/) or [Visual Studio Code](https://code.visualstudio.com/)
- Two terminals so that the silo and client can run at the same time

## Create the solution

Create a solution with four Orleans projects:

- **GrainInterfaces** contains grain contracts shared by callers and implementations.
- **Grains** contains the grain implementations.
- **Silo** hosts the Orleans runtime and grain activations.
- **Client** is an external process that connects to the silo and calls grains.

The `Microsoft.Orleans.Templates` package creates this layout and adds an Aspire AppHost that orchestrates the silo, client, and Azurite-backed Azure Storage resources:

```dotnetcli
dotnet new install Microsoft.Orleans.Templates
dotnet new orleans --name OrleansHelloWorld --output OrleansHelloWorld
```

Continue with the manual steps below to learn how the projects and references fit together.

Run the following commands in an empty directory:

```dotnetcli
dotnet new sln --name OrleansHelloWorld --format slnx
dotnet new classlib --name GrainInterfaces --framework net10.0
dotnet new classlib --name Grains --framework net10.0
dotnet new console --name Silo --framework net10.0
dotnet new console --name Client --framework net10.0

dotnet solution OrleansHelloWorld.slnx add GrainInterfaces/GrainInterfaces.csproj
dotnet solution OrleansHelloWorld.slnx add Grains/Grains.csproj
dotnet solution OrleansHelloWorld.slnx add Silo/Silo.csproj
dotnet solution OrleansHelloWorld.slnx add Client/Client.csproj

dotnet reference add GrainInterfaces/GrainInterfaces.csproj --project Grains/Grains.csproj
dotnet reference add Grains/Grains.csproj --project Silo/Silo.csproj
dotnet reference add GrainInterfaces/GrainInterfaces.csproj --project Client/Client.csproj

dotnet package add Microsoft.Orleans.Sdk --version 10.2.2 --project GrainInterfaces/GrainInterfaces.csproj
dotnet package add Microsoft.Orleans.Sdk --version 10.2.2 --project Grains/Grains.csproj
dotnet package add Microsoft.Orleans.Server --version 10.2.2 --project Silo/Silo.csproj
dotnet package add Microsoft.Orleans.Client --version 10.2.2 --project Client/Client.csproj
dotnet package add Microsoft.Extensions.Hosting --version 10.0.9 --project Silo/Silo.csproj
dotnet package add Microsoft.Extensions.Hosting --version 10.0.9 --project Client/Client.csproj
```

The client references only **GrainInterfaces**. It doesn't need the grain implementation assembly. The silo references **Grains**, which in turn references **GrainInterfaces**.

## Define the grain contract

Delete _GrainInterfaces/Class1.cs_, create _GrainInterfaces/IHello.cs_, and add the following grain interface:

:::code source="snippets/hello-world/GrainInterfaces/IHello.cs" id="grain-interface":::

<xref:Orleans.IGrainWithStringKey> identifies the grain by a string key. Grain contracts use asynchronous return types because calls can cross process and network boundaries.

## Implement the grain

Delete _Grains/Class1.cs_, create _Grains/HelloGrain.cs_, and add the following implementation:

:::code source="snippets/hello-world/Grains/HelloGrain.cs" id="grain-implementation":::

The implementation inherits from <xref:Orleans.Grain> and implements `IHello`. Orleans source generators discover the grain contract and implementation at build time, so you don't need to register application parts manually.

## Configure the silo

Replace _Silo/Program.cs_ with the following code:

:::code source="snippets/hello-world/Silo/Program.cs" id="first_orleans_app_silo_program":::

<xref:Microsoft.Extensions.Hosting.OrleansSiloGenericHostExtensions.UseOrleans*> adds the Orleans silo to the [.NET Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host). <xref:Orleans.Hosting.CoreHostingExtensions.UseLocalhostClustering*> configures development-only clustering and gateway endpoints on the local machine.

The silo project references **Grains**, so the runtime can discover and activate `HelloGrain`.

## Configure the external client

Replace _Client/Program.cs_ with the following code:

:::code source="snippets/hello-world/Client/Program.cs" id="first_orleans_app_client_program":::

<xref:Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient*> adds an external Orleans client to the Generic Host. The client uses the same localhost clustering configuration as the silo. After the host starts, the client resolves <xref:Orleans.IGrainFactory> from dependency injection, obtains a grain reference, and invokes the grain.

## Build and run the application

Build the solution:

```dotnetcli
dotnet build OrleansHelloWorld.slnx
```

Start the silo in the first terminal:

```dotnetcli
dotnet run --project Silo
```

Wait until the silo prints `Application started`, then start the client in the second terminal:

```dotnetcli
dotnet run --project Client
```

The client prints the grain response:

```output
Hello, Hi friend!
```

The client never creates or locates a `HelloGrain` object directly. `GetGrain<IHello>("friend")` returns a logical reference. When the client invokes `SayHello`, Orleans routes the call through a silo gateway and activates the grain if it isn't already active.

Stop the silo by pressing <kbd>Ctrl</kbd>+<kbd>C</kbd> in its terminal.

## Next steps

- [Choose between a co-hosted and external client](../host/client.md)
- [Configure Orleans for production](../host/configuration-guide/index.md)
- [Learn more about grain identity](../grains/grain-identity.md)
- [Browse maintained samples](../tutorials-and-samples/index.md)
