---
title: Minimal Orleans app sample project
description: Explore the minimal Orleans app sample project.
ms.date: 03/30/2025
ms.topic: tutorial
zone_pivot_groups: orleans-version
---

# Tutorial: Create a minimal Orleans application

In this tutorial, follow step-by-step instructions to create the foundational moving parts common to most Orleans applications. It's designed to be self-contained and minimalistic.

This tutorial lacks appropriate error handling and other essential code useful for a production environment. However, it should help you gain a hands-on understanding of the common Orleans app structure and allow you to focus your continued learning on the parts most relevant to you.

:::zone target="docs" pivot="orleans-8-0,orleans-9-0,orleans-10-0"

> [!TIP]
> For production-ready Orleans applications, consider using **Aspire** to simplify resource management, service discovery, observability, and deployment. Aspire handles configuration for clustering, grain storage, reminders, and streaming automatically. See [Aspire Orleans integration](../host/aspire-integration.md) to learn more.

:::zone-end

:::zone target="docs" pivot="orleans-7-0,orleans-3-x"

<!-- This zone intentionally left empty to satisfy pivot validation -->

:::zone-end

## Prerequisites

:::zone target="docs" pivot="orleans-8-0,orleans-9-0,orleans-10-0"

- [Visual Studio 2026 or later](https://visualstudio.microsoft.com/downloads)
- [The latest .NET SDK](https://dotnet.microsoft.com/download/dotnet)

:::zone-end

:::zone target="docs" pivot="orleans-7-0,orleans-3-x"

- [Visual Studio 2022 or later](https://visualstudio.microsoft.com/downloads)
- [.NET SDK compatible with your target Orleans version](https://dotnet.microsoft.com/download/dotnet)

:::zone-end

## Project setup

For this tutorial, create four projects as part of the same solution:

- Library to contain the grain interfaces.
- Library to contain the grain classes.
- Console app to host the Silo.
- Console app to host the Client.

### Create the structure in Visual Studio

Replace the default code with the code provided for each project.

1. Start by creating a Console App project in a new solution. Call the project `Silo` and name the solution `OrleansHelloWorld`. For more information on creating a console app, see [Tutorial: Create a .NET console application](../../core/tutorials/create-console-app.md).
1. Add another Console App project and name it `Client`.
1. Add a Class Library and name it `GrainInterfaces`. For information on creating a class library, see [Tutorial: Create a .NET class library](../../core/tutorials/create-class-library.md).
1. Add another Class Library and name it `Grains`.

#### Delete default source files

1. Delete _Class1.cs_ from **Grains**.
1. Delete _Class1.cs_ from **GrainInterfaces**.

### Add references

1. **Grains** references **GrainInterfaces**.
1. **Silo** references **Grains**.
1. **Client** references **GrainInterfaces**.

## Add Orleans NuGet packages

| Project | NuGet package |
|---|---|
| Silo | `Microsoft.Orleans.Server`<br>`Microsoft.Extensions.Logging.Console`<br>`Microsoft.Extensions.Hosting` |
| Client | `Microsoft.Orleans.Client`<br>`Microsoft.Extensions.Logging.Console`<br>`Microsoft.Extensions.Hosting` |
| Grain Interfaces | `Microsoft.Orleans.Sdk` |
| Grains | `Microsoft.Orleans.Sdk`<br>`Microsoft.Extensions.Logging.Abstractions` |

`Microsoft.Orleans.Server`, `Microsoft.Orleans.Client`, and `Microsoft.Orleans.Sdk` are metapackages that bring dependencies you'll most likely need on the Silo and client. For more information on adding package references, see [dotnet package add](../../core/tools/dotnet-package-add.md) or [Install and manage packages in Visual Studio using the NuGet Package Manager](/nuget/consume-packages/install-use-packages-visual-studio).

## Define a grain interface

In the **GrainInterfaces** project, add an _IHello.cs_ code file and define the following `IHello` interface:

:::code source="snippets/minimal/GrainInterfaces/IHello.cs":::

## Define a grain class

In the **Grains** project, add a _HelloGrain.cs_ code file and define the following class:

:::code source="snippets/minimal/Grains/HelloGrain.cs":::

### Create the silo

To create the Silo project, add code to initialize a server that hosts and runs the grains—a silo. Use the localhost clustering provider, which allows running everything locally without depending on external storage systems. For more information, see [Local Development Configuration](../host/configuration-guide/local-development-configuration.md). In this example, run a cluster with a single silo.

Add the following code to _Program.cs_ of the **Silo** project:

:::code source="snippets/minimal/Silo/Program.cs":::

The preceding code:

- Configures the <xref:Microsoft.Extensions.Hosting.IHost> to use Orleans with the <xref:Microsoft.Extensions.Hosting.GenericHostExtensions.UseOrleans*> method.
- Specifies using the localhost clustering provider with the <xref:Orleans.Hosting.CoreHostingExtensions.UseLocalhostClustering(Orleans.Hosting.ISiloBuilder,System.Int32,System.Int32,System.Net.IPEndPoint,System.String,System.String)> method.
- Runs the `host` and waits for the process to terminate by listening for <kbd>Ctrl</kbd>+<kbd>C</kbd> or `SIGTERM`.

### Create the client

Finally, configure a client to communicate with the grains, connect it to the cluster (with a single silo), and invoke the grain. The clustering configuration must match the one used for the silo. For more information, see [Clusters and Clients](../host/client.md).

:::code source="snippets/minimal/Client/Program.cs":::

## Run the application

Build the solution and run the **Silo**. After receiving the confirmation message that the **Silo** is running, run the **Client**.

To start the Silo from the command line, run the following command from the directory containing the Silo's project file:

```dotnetcli
dotnet run
```

You'll see numerous outputs as part of the Silo startup. After seeing the following message, you're ready to run the client:

```Output
Application started. Press Ctrl+C to shut down.
```

From the client project directory, run the same .NET CLI command in a separate terminal window to start the client:

```dotnetcli
dotnet run
```

For more information on running .NET apps, see [dotnet run](../../core/tools/dotnet-run.md). If you're using Visual Studio, you can [configure multiple startup projects](/visualstudio/ide/how-to-set-multiple-startup-projects).

## See also

- [List of Orleans packages](../resources/nuget-packages.md)
- [Orleans configuration guide](../host/configuration-guide/index.md)
- [Orleans best practices](https://www.microsoft.com/research/publication/orleans-best-practices)
