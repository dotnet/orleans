---
title: 'Quickstart: Orleans Hello World'
description: Build and run the bare minimum Orleans application.
ms.date: 08/08/2026
ms.topic: quickstart
ms.devlang: csharp
---

# Quickstart: Orleans Hello World

This quickstart builds the bare minimum Orleans application in one project. The process hosts a silo and calls a grain using the client that Orleans provides inside every silo.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- An editor such as [Visual Studio](https://visualstudio.microsoft.com/) or [Visual Studio Code](https://code.visualstudio.com/)

## Create the project

Run the following commands in an empty directory:

```dotnetcli
dotnet new console --name HelloWorld --framework net10.0
cd HelloWorld
dotnet package add Microsoft.Orleans.Server --version 10.2.2
dotnet package add Microsoft.Extensions.Hosting --version 10.0.9
```

[`Microsoft.Orleans.Server`](https://www.nuget.org/packages/Microsoft.Orleans.Server) includes the Orleans runtime, client APIs, and SDK build tooling.

## Define the grain contract

Create _IHello.cs_ and define a grain interface:

:::code source="snippets/hello-world/IHello.cs" id="hello_world_grain_interface":::

<xref:Orleans.IGrainWithStringKey> identifies the grain by a string key. Grain contracts use asynchronous return types because calls can cross process and network boundaries.

## Implement the grain

Create _HelloGrain.cs_ and implement the grain interface:

:::code source="snippets/hello-world/HelloGrain.cs" id="hello_world_grain":::

## Configure Orleans and call the grain

Replace _Program.cs_ with the following code:

:::code source="snippets/hello-world/Program.cs" id="hello_world_program":::

<xref:Microsoft.Extensions.Hosting.OrleansSiloGenericHostExtensions.UseOrleans*> adds a silo and its in-process client to the [.NET Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host). <xref:Orleans.Hosting.CoreHostingExtensions.UseLocalhostClustering*> configures development-only clustering on the local machine.

After the host starts, resolve <xref:Orleans.IGrainFactory>, obtain a logical reference to the grain identified by `friend`, and call it.

## Run the app

```dotnetcli
dotnet run
```

The app prints:

```output
Hello, Hi friend!
```

## Next steps

- [Build your first Orleans app](../quickstarts/build-your-first-orleans-app.md) using a typical multi-project structure.
- [Understand Orleans concepts](../overview.md).
- [Browse maintained samples](index.md).
