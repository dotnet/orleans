---
layout: page
title: Samples Overview
---
{% include JB/setup %}

### What do I need?
To productively use the Orleans samples, you need to have a copy of Visual Studio 2012 or 2013. Trial versions of Visual Studio are available at [MSDN](http://msdn.microsoft.com/en-us/dn369242#fbid=S8c2-uibvsG). Note that the Express versions of Visual Studio do not support extension packages.

An Azure subscription will help with some of the samples, but is not required. Azure is available as a [free trial](http://www.windowsazure.com/en-us/pricing/free-trial/). For the Azure-based samples, you will need to install the [Azure SDK 2.4 or 2.5 for .NET](http://www.windowsazure.com/en-us/downloads). 

The samples themselves can be downloaded from [GitHub](https://github.com/dotnet/orleans/tree/master/Samples).


### [Hello World](Hello-World)

This is the Orleans version of an old classic. It demonstrates that while there is no such thing as "trivial" when you are dealing with distributed computing, Orleans makes it pretty straight-forward.

### [Azure Web Sample](Azure-Web-Sample)

An Azure-hosted version of Hello World.

### [Adventure](Adventure)

Before there was graphical user interfaces, before the era of game consoles and massive-multiplayer games, there were VT100 terminals and there was [Colossal Cave Adventure](http://en.wikipedia.org/wiki/Colossal_Cave_Adventure). Possibly lame by today's standards, back then it was a magical world of monsters, chirping birds, and things you could pick up. It's the inspiration for this sample.

### [Presence Service](Presence-Service)

This sample shows the principles behind a typical (though much simplified) presence service, such as you would find in online games and other social applications.

### [Tic Tac Toe](Tic-Tac-Toe)

This is a simple online version of the classic board game.

### [Chirper](Chirper)

A simple social network pub/sub system, with chirp messages being sent from publishers to followers.

### [Twitter Sentiment](Twitter-Sentiment)

This sample uses Orleans to aggregate and analyze twitter data for a simple sentiment dashboard.
Twitter Sentiment relies on a Node.js project for some of its functionality, as well as Twitter developer credentials. To use this sample, you will need to:

1. Get the Node.js Tools for Visual Studio 
2. Get a Twitter account 
3. Sign up as a Twitter Developer. 

### [GPS Tracker](GPS-Tracker)

A combination of Orleans and SignalR is used to simulate GPS devices tracked as they move around San Francisco, updating their locations as they change.

### [Storage Providers](Storage-Providers)

This sample contains sample code for two Orleans storage providers: one that stores data in a regular file system, one that connects to MongoDB.

