---
title: Adventure game sample project
description: Explore the Adventure sample project written with .NET Orleans.
ms.date: 03/30/2025
ms.topic: sample
---

# Adventure game sample project

This sample is a simple multiplayer text adventure game inspired by old-fashioned, text-based adventure games.

## Instructions

1. Navigate to the [Orleans Text Adventure Game](/samples/dotnet/samples/orleans-text-adventure-game) in the samples browser experience.
1. Select **Browse code** to view the source code.
1. Clone the source code and build the solution.
1. Start the _AdventureServer_ first, then the _AdventureClient_.
1. You are then prompted to enter your name on the command line. Enter it and begin the game.

For more information, see [Building the sample](/samples/dotnet/samples/orleans-text-adventure-game#building-the-sample).

## Overview

The _AdventureServer_ program starts by reading an _AdventureMap.json_ file.

It sets up a series of "rooms" such as a forest, beach, caves, a clearing, etc. These locations connect to other rooms to model the places and layout of the game. The sample configuration describes only a handful of locations.

Rooms can contain "things" such as keys, swords, etc.

The _AdventureClient_ program sets up your player and provides a simple text-based user interface allowing you to play the game.

You can move around rooms and interact with things using a simple command language, saying things such as "go north" or "take brass key".

## Why Orleans?

Orleans allows you to describe the game using very simple C# code while enabling it to scale to a massively multiplayer game. For this motivation to be meaningful, the labyrinth of rooms needs to be very large and support many simultaneous players. One value of Orleans is that you can design the service for growth; the overhead of running it at a small scale isn't significant, and you can remain confident it will scale if the need arises.

## How is it modeled?

Players and Rooms are modeled as grains. These grains allow you to distribute the game, with each grain modeling state and functionality.

Things such as keys are modeled as plain old objects—they are just simple immutable data structures that move around rooms and among players; they don't need to be grains.

## Possible improvements

1. Make the map much, much bigger.
1. Make the brass key unlock something.
1. Allow players to message each other.
1. Make eating food and drinking water possible and meaningful.
