---
title: Orleans Hello World
description: Choose a guided Orleans beginner quickstart or the compact Hello World sample.
ms.date: 08/07/2026
ms.topic: tutorial
---

# Orleans Hello World

Choose the path that matches how you want to learn:

- For a guided walkthrough, follow [Build your first Orleans app](../quickstarts/build-your-first-orleans-app.md). It covers hosting a silo, defining and implementing a grain, obtaining a grain reference, and persisting state from ASP.NET Core.
- To learn how contracts, grain implementations, silos, and external clients fit into separate projects, follow [Create a multi-project Orleans application](multi-project-orleans-application.md).
- For the smallest complete console application, inspect or run the maintained [Hello World sample](https://github.com/dotnet/orleans/tree/main/samples/HelloWorld).

The quickstart is the canonical first tutorial. The multi-project tutorial adds an external client and explicit dependency boundaries. The repository sample is the source of truth for the compact Hello World implementation.

<a id="project-setup"></a>

## Choose a project structure

The quickstart and Hello World sample use one project so that beginners can focus on Orleans concepts before introducing a multi-project architecture. Applications can separate grain contracts, implementations, hosts, and clients into projects as their deployment and dependency boundaries require.
