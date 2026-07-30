---
title: Orleans sample projects
description: Explore the various sample projects written with .NET Orleans.
ms.date: 03/30/2025
ms.topic: sample
---

# Orleans sample projects

## [Hello, World!](/samples/dotnet/samples/orleans-hello-world-sample-app)

<!-- markdownlint-disable-file MD034 -->

:::image type="content" source="https://raw.githubusercontent.com/dotnet/samples/main/orleans/HelloWorld/code.png" alt-text="Sample code for the Hello World Orleans app.":::

A *Hello, World!* application demonstrating how to create and use your first grains.

### Hello World demonstrates

- How to get started with Orleans
- How to define and implement a grain interface
- How to get a reference to a grain and call it

## [Shopping Cart](/samples/dotnet/samples/orleans-shopping-cart-app-sample)

:::image type="content" source="https://raw.githubusercontent.com/dotnet/samples/main/orleans/ShoppingCart/media/shopping-cart.png" alt-text="Screen capture from the Shopping Cart Orleans sample app.":::

A canonical shopping cart sample application built using Microsoft Orleans. This app shows the following features:

- **Shopping cart**: A simple shopping cart application using Orleans for its cross-platform framework support and scalable distributed application capabilities.

  - **Inventory management**: Edit and/or create product inventory.
  - **Shop inventory**: Explore purchasable products and add them to your cart.
  - **Cart**: View a summary of all items in your cart and manage these items by removing or changing the quantity of each item.

### Shopping cart demonstrates

- How to create a distributed shopping cart experience
- How to manage grain persistence as it relates to live inventory updates
- How to expose user-specific items spanning multiple clients

## [Adventure](/samples/dotnet/samples/orleans-text-adventure-game)

:::image type="content" source="https://raw.githubusercontent.com/dotnet/samples/main/orleans/Adventure/assets/BoxArt.jpg" alt-text="Cover art for the Adventure Orleans app.":::

Before graphical user interfaces, game consoles, and massive-multiplayer games, there were VT100 terminals and games like [Colossal Cave Adventure](https://en.wikipedia.org/wiki/Colossal_Cave_Adventure), [Zork](https://en.wikipedia.org/wiki/Zork), and [Microsoft Adventure](https://en.wikipedia.org/wiki/Microsoft_Adventure). Possibly bland by today's standards, back then it was a magical world of monsters, chirping birds, and things you could pick up. This sample draws inspiration from those games.

### Adventure demonstrates

- How to structure an application (in this case, a game) using grains
- How to connect an external client to an Orleans cluster (<xref:Orleans.ClientBuilder>)

## [Chirper](/samples/dotnet/samples/orleans-chirper-social-media-sample-app)

:::image type="content" source="https://raw.githubusercontent.com/dotnet/samples/main/orleans/Chirper/screenshot.png" alt-text="Sample code for the Chirper Orleans app.":::

A social network pub/sub system where users send short text messages to each other. Publishers send out short *"Chirp"* messages (not to be confused with *"Tweets"* for various legal reasons) to any other users following them.

### Chirper demonstrates

- How to build a simplified social media / social network application using Orleans
- How to store state within a grain using grain persistence (<xref:Orleans.Runtime.IPersistentState`1>)
- Grains implementing multiple grain interfaces
- Reentrant grains, allowing multiple grain calls to execute concurrently in a single-threaded, interleaving fashion
- Using a *grain observer* (<xref:Orleans.IGrainObserver>) to receive push notifications from grains

## [GPS Tracker](/samples/dotnet/samples/orleans-gps-device-tracker-sample)

:::image type="content" source="https://raw.githubusercontent.com/dotnet/samples/main/orleans/GPSTracker/screenshot.jpeg" alt-text="Sample code for the GPS Orleans app.":::

A service for tracking GPS-equipped [IoT](/dotnet/iot) devices on a map. Device locations update in near-real-time using SignalR, demonstrating one approach to integrating Orleans with SignalR. The device updates originate from a *device gateway*, implemented using a separate process that connects to the main service and simulates several devices moving pseudorandomly around an area of San Francisco.

### GPS Tracker demonstrates

- How to use Orleans to build an [Internet of Things](/dotnet/iot) application
- How Orleans can be co-hosted and integrated with [ASP.NET Core SignalR](/aspnet/core/signalr/introduction)
- How to broadcast real-time updates from a grain to a set of clients using Orleans and SignalR

## [HanBaoBao](https://github.com/ReubenBond/hanbaobao-web)

:::image type="content" source="https://raw.githubusercontent.com/ReubenBond/hanbaobao-web/main/assets/demo-1.png" alt-text="HanBaoBao - Orleans sample application screen capture.":::

An English-Mandarin dictionary web application demonstrating deployment to Kubernetes, fan-out grain calls, and request throttling.

### HanBaoBao demonstrates

- How to build a realistic application using Orleans
- How to deploy an Orleans-based application to Kubernetes
- How to integrate Orleans with ASP.NET Core and a [*Single-page Application*](https://en.wikipedia.org/wiki/Single-page_application) JavaScript framework ([Vue.js](https://vuejs.org/))
- How to implement leaky-bucket request throttling
- How to load and query data from a database
- How to cache results lazily and temporarily
- How to fan out requests to many grains and collect the results

## [Presence Service](/samples/dotnet/samples/orleans-gaming-presence-service-sample)

:::image type="content" source="https://raw.githubusercontent.com/dotnet/samples/main/orleans/Presence/screenshot.png" alt-text="Output from the Presence Service Orleans app.":::

A gaming presence service, similar to one of the Orleans-based services built for [Halo](https://www.xbox.com/games/halo). A presence service tracks players and game sessions in near-real-time.

### Presence Service demonstrates

- A simplified version of a real-world use of Orleans
- Using a *grain observer* (<xref:Orleans.IGrainObserver>) to receive push notifications from grains

## [Tic Tac Toe](/samples/dotnet/samples/orleans-tictactoe-web-based-game)

:::image type="content" source="https://raw.githubusercontent.com/dotnet/samples/main/orleans/TicTacToe/logo.png" alt-text="Logo from the Tic Tac Toe Orleans sample app.":::

A web-based [Tic-tac-toe](https://en.wikipedia.org/wiki/Tic-tac-toe) game using ASP.NET MVC, JavaScript, and Orleans.

### Tic Tac Toe demonstrates

- How to build an online game using Orleans
- How to build a basic game lobby system
- How to access Orleans grains from an ASP.NET Core MVC application

## [Voting](/samples/dotnet/samples/orleans-voting-sample-app-on-kubernetes)

:::image type="content" source="https://raw.githubusercontent.com/dotnet/samples/main/orleans/Voting/screenshot.png" alt-text="Screen capture from Voting Orleans sample app.":::

A web application for voting on a set of choices. This sample demonstrates deployment to Kubernetes. The application uses the [.NET generic host](../../core/extensions/generic-host.md) to co-host [ASP.NET Core](/aspnet/core) and Orleans, as well as the [Orleans Dashboard](https://github.com/OrleansContrib/OrleansDashboard), together in the same process.

:::image type="content" source="https://raw.githubusercontent.com/dotnet/samples/main/orleans/Voting/dashboard.png" alt-text="The Orleans dashboard running as part of the Voting sample app.":::

### Voting demonstrates

- How to deploy an Orleans-based application to Kubernetes
- How to configure the [Orleans Dashboard](https://github.com/OrleansContrib/OrleansDashboard)

## [Chat Room](/samples/dotnet/samples/orleans-chat-room-sample)

:::image type="content" source="https://raw.githubusercontent.com/dotnet/samples/main/orleans/ChatRoom/screenshot.png" alt-text="Sample output from the running Chat Room sample Orleans app.":::

A terminal-based chat application built using [Orleans Streams](../streaming/index.md).

### Chat Room demonstrates

- How to build a chat application using Orleans
- How to use [Orleans Streams](../streaming/index.md)

## [Bank Account](/samples/dotnet/samples/orleans-bank-account-acid-transactions)

:::image type="content" source="https://raw.githubusercontent.com/dotnet/samples/main/orleans/BankAccount/BankClient.png" alt-text="Output from the running Bank Account client sample Orleans app.":::

Simulates bank accounts, using ACID transactions to transfer random amounts between a set of accounts.

### Bank Account demonstrates

- How to use Orleans Transactions to safely perform operations involving multiple stateful grains with ACID guarantees and serializable isolation.

## [Blazor Server](/samples/dotnet/samples/orleans-aspnet-core-blazor-server-sample) and [Blazor WebAssembly](/samples/dotnet/samples/orleans-aspnet-core-blazor-wasm-sample)

:::image type="content" source="https://raw.githubusercontent.com/dotnet/samples/main/orleans/Blazor/BlazorServer/screenshot.png" alt-text="Blazor Orleans sample app screen capture":::

These two Blazor samples are based on the [Blazor introductory tutorials](https://dotnet.microsoft.com/learn/aspnet/blazor-tutorial/intro), adapted for use with Orleans. The [Blazor WebAssembly](https://github.com/dotnet/samples/tree/main/orleans/Blazor/BlazorWasm) sample uses the [Blazor WebAssembly hosting model](/aspnet/core/blazor/hosting-models#blazor-webassembly). The [Blazor Server](https://github.com/dotnet/samples/tree/main/orleans/Blazor/BlazorServer) sample uses the [Blazor Server hosting model](/aspnet/core/blazor/hosting-models#blazor-server). They include an interactive counter, a TODO list, and a Weather service.

### Blazor sample apps demonstrate

- How to integrate ASP.NET Core Blazor Server with Orleans
- How to integrate ASP.NET Core Blazor WebAssembly (WASM) with Orleans

## [Stocks](/samples/dotnet/samples/orleans-stocks-sample-app)

:::image type="content" source="https://raw.githubusercontent.com/dotnet/samples/main/orleans/Stocks/screenshot.png" alt-text="Output from the running Stocks client sample Orleans app.":::

A stock price application that fetches prices from a remote service using an HTTP call and caches prices temporarily in a grain. A [BackgroundService](../../core/extensions/workers.md) periodically polls for updated stock prices from various `StockGrain` grains corresponding to a set of stock symbols.

### Stocks sample app demonstrates

- How to use Orleans from within a [BackgroundService](../../core/extensions/workers.md).
- How to use timers within a grain.
- How to make external service calls using .NET's `HttpClient` and cache the results within a grain.

## [Transport Layer Security](/samples/dotnet/samples/orleans-transport-layer-security-tls)

:::image type="content" source="https://raw.githubusercontent.com/dotnet/samples/main/orleans/TransportLayerSecurity/screenshot.png" alt-text="Output from the running TLS sample Orleans app.":::

A *Hello, World!* application configured to use mutual [*Transport Layer Security*](https://en.wikipedia.org/wiki/Transport_Layer_Security) to secure network communication between every server.

### Transport Layer Security demonstrates

- How to configure mutual-TLS (mTLS) authentication for Orleans

## [Visual Basic Hello World](/samples/dotnet/samples/orleans-vb-sample/)

A *Hello, World!* application using Visual Basic.

### Visual Basic Hello World demonstrates

- How to develop Orleans-based applications using Visual Basic

## [F# Hello World](/samples/dotnet/samples/orleans-fsharp-sample)

A *Hello, World!* application using F#.

### F# Hello World demonstrates

- How to develop Orleans-based applications using F#

## [Streaming: Pub/Sub Streams over Azure Event Hubs](/samples/dotnet/samples/orleans-streaming-samples)

An application using Orleans Streams with [Azure Event Hubs](https://azure.microsoft.com/services/event-hubs/) as the provider and implicit subscribers.

### Pub/Sub Streams demonstrates

- How to use [Orleans Streams](../streaming/index.md)
- How to use the `[ImplicitStreamSubscription(namespace)]` attribute to implicitly subscribe a grain to the stream with the corresponding ID
- How to configure Orleans Streams for use with [Azure Event Hubs](https://azure.microsoft.com/services/event-hubs/)

## [Streaming: Custom Data Adapter](/samples/dotnet/samples/orleans-streaming-samples)

An application using Orleans Streams with a non-Orleans publisher pushing to a stream that a grain consumes via a *custom data adapter*, telling Orleans how to interpret stream messages.

### Custom Data Adapter demonstrates

- How to use [Orleans Streams](../streaming/index.md)
- How to use the `[ImplicitStreamSubscription(namespace)]` attribute to implicitly subscribe a grain to the stream with the corresponding ID
- How to configure Orleans Streams for use with [Azure Event Hubs](https://azure.microsoft.com/services/event-hubs/)
- How to consume stream messages published by non-Orleans publishers by providing a custom `EventHubDataAdapter` implementation (a custom data adapter)
