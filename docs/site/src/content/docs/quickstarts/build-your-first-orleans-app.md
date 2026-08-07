---
title: 'Quickstart: Build your first Orleans app'
description: Build an Orleans URL shortener with ASP.NET Core.
ms.date: 08/02/2026
ms.topic: quickstart
ms.devlang: csharp
---

# Quickstart: Build your first Orleans app

This quickstart is the canonical beginner path for Orleans. You build a URL shortener in one ASP.NET Core process, define a grain, persist its state in memory for local development, and call it from [Minimal API](https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis) endpoints.

You learn how to:

- Add Orleans to an ASP.NET Core application.
- Configure a local silo.
- Define and implement a grain.
- Obtain a grain reference and call it.
- Persist grain state using a named provider.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- An editor such as [Visual Studio](https://visualstudio.microsoft.com/) or [Visual Studio Code](https://code.visualstudio.com/)

## Create the application

Run these [.NET CLI](https://learn.microsoft.com/dotnet/core/tools/) commands in a terminal:

```dotnetcli
dotnet new web -n OrleansURLShortener -f net10.0
cd OrleansURLShortener
dotnet package add Microsoft.Orleans.Server
```

[`Microsoft.Orleans.Server`](https://www.nuget.org/packages/Microsoft.Orleans.Server) is the metapackage for an application that hosts a silo. It also includes the Orleans SDK and client APIs, so this single-project application can define grains and call them. For details about the commands, see [`dotnet new`](https://learn.microsoft.com/dotnet/core/tools/dotnet-new) and [`dotnet package add`](https://learn.microsoft.com/dotnet/core/tools/dotnet-package-add).

## Configure Orleans

Replace the generated contents of _Program.cs_. Begin by adding Orleans to the host before `builder.Build()`:

:::code source="snippets/url-shortener/orleansurlshortener/Program.cs" id="configuration":::

<xref:Microsoft.Extensions.Hosting.GenericHostExtensions.UseOrleans*> adds a silo to the .NET host. <xref:Orleans.Hosting.CoreHostingExtensions.UseLocalhostClustering*> configures a single-machine development cluster, and <xref:Orleans.Hosting.MemoryGrainStorageSiloBuilderExtensions.AddMemoryGrainStorage*> registers a storage provider named `urls`.

> [!IMPORTANT]
> Localhost clustering and memory storage are development settings. Memory storage is lost when the process stops and isn't shared across silos. Production deployments need a shared clustering provider and, when state must survive process loss, a durable storage provider.

## Define the grain contract

A grain contract is an interface whose methods are asynchronous and which identifies the grain's key type. Append this interface to _Program.cs_:

:::code source="snippets/url-shortener/orleansurlshortener/Program.cs" id="graininterface":::

Each short code is the string key of one `IUrlShortenerGrain`.

## Implement the grain

Append the grain implementation and its state type:

:::code source="snippets/url-shortener/orleansurlshortener/Program.cs" id="grain":::

The constructor injects <xref:Orleans.Runtime.IPersistentState`1> associated with the `urls` provider. Assigning `state.State` changes only the in-memory value. Calling <xref:Orleans.Core.IStorage.WriteStateAsync*> writes it to the configured provider.

The <xref:Orleans.GenerateSerializerAttribute> and <xref:Orleans.IdAttribute> attributes let Orleans generate a version-tolerant serializer for `UrlDetails`. Keep existing field IDs stable when the type evolves.

## Add the endpoints

Add the endpoints before `app.Run()`:

:::code source="snippets/url-shortener/orleansurlshortener/Program.cs" id="endpoints":::

The `/shorten` endpoint:

1. Validates the destination URL.
1. Creates a short code.
1. Gets a grain reference using that code as its identity.
1. Calls the grain to store the destination.

The `/go/{shortenedRouteSegment}` endpoint gets the same logical grain by key, reads its state, and redirects the caller.

## Run the application

The completed _Program.cs_ should match the maintained documentation sample:

:::code source="snippets/url-shortener/orleansurlshortener/Program.cs":::

Start the app:

```dotnetcli
dotnet run
```

Use the address printed by ASP.NET Core to create a short URL:

```text
http://localhost:<port>/shorten?url=https://learn.microsoft.com/dotnet/orleans
```

Open the returned `/go/...` URL and verify that it redirects to the Orleans documentation.

## What happened

The first call to a short-code grain caused Orleans to activate it. Orleans routed later calls with the same key to that activation. The grain explicitly wrote its state through the named provider.

This application runs as one process, but its contracts don't encode a server location. Moving to multiple silos is primarily a hosting and provider configuration task; the grain still uses the same identity and interface.

## Next steps

- [Understand Orleans concepts](../overview.md)
- [Choose Orleans packages](../resources/nuget-packages.md)
- [Configure Orleans for production](../host/configuration-guide/index.md)
- [Browse maintained samples](../tutorials-and-samples/index.md)
