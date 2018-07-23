---
layout: page
title: Samples Overview
---

[!include[](../../warning-banner.md)]

# Samples Overview

## What do I need?

To productively use the Orleans samples, please follow the [Prerequisites](../Installation/Prerequisites.md) section for the supported versions of the .NET framework, Visual Studio and Azure SDK.
An Azure subscription will help with some of the samples, but is not required. For the Azure-based samples, you will need to install the SDK.

The samples themselves can be downloaded from [GitHub](https://github.com/dotnet/orleans/tree/master/Samples).


## [Hello World](Hello-World.md)

This is the Orleans version of an old classic. It demonstrates that while there is no such thing as "trivial" when you are dealing with distributed computing, Orleans makes it pretty straight-forward.

## [Azure Web Sample](Azure-Web-Sample.md)

An Azure-hosted version of Hello World.

## [Adventure](Adventure.md)

Before there was graphical user interfaces, before the era of game consoles and massive-multiplayer games, there were VT100 terminals and there was [Colossal Cave Adventure](http://en.wikipedia.org/wiki/Colossal_Cave_Adventure). Possibly lame by today's standards, back then it was a magical world of monsters, chirping birds, and things you could pick up. It's the inspiration for this sample.

## [Presence Service](Presence-Service.md)

This sample shows the principles behind a typical (though much simplified) presence service, such as you would find in online games and other social applications.

## [Tic Tac Toe](Tic-Tac-Toe.md)

This is a simple online version of the classic board game.

## [Chirper](Chirper.md)

A simple social network pub/sub system, with chirp messages being sent from publishers to followers.

## [Twitter Sentiment](Twitter-Sentiment.md)

This sample uses Orleans to aggregate and analyze twitter data for a simple sentiment dashboard.
Twitter Sentiment relies on a Node.js project for some of its functionality, as well as Twitter developer credentials. To use this sample, you will need to:

1. Get the Node.js Tools for Visual Studio
2. Get a Twitter account
3. Sign up as a Twitter Developer.

## [GPS Tracker](GPS-Tracker.md)

A combination of Orleans and SignalR is used to simulate GPS devices tracked as they move around San Francisco, updating their locations as they change.

## [Storage Providers](Storage-Providers.md)

This sample contains sample code for two Orleans storage providers: one that stores data in a regular file system, one that connects to MongoDB.
