---
title: Create your first Orleans application
description: Start with the canonical Orleans beginner quickstart.
ms.date: 08/02/2026
ms.topic: tutorial
---

# Create your first Orleans application

The former four-project minimal tutorial duplicated the same concepts as the canonical beginner experience while requiring extra project structure.

Follow [Build your first Orleans app](../quickstarts/build-your-first-orleans-app.md) for the canonical walkthrough. It covers hosting a silo, defining and implementing a grain, obtaining a grain reference, and persisting state from ASP.NET Core.

For a compact console application, use the maintained [Hello World sample](https://github.com/dotnet/orleans/tree/main/samples/HelloWorld).

## Project setup

The canonical quickstart uses one ASP.NET Core project so that beginners can focus on Orleans concepts before introducing a multi-project architecture. Production applications can separate grain contracts, implementations, hosts, and clients into projects as their deployment and dependency boundaries require.
