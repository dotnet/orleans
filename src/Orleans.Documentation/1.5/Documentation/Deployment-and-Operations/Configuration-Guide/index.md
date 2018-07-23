---
layout: page
title: Orleans Configuration Guide
---

[!include[](../../../warning-banner.md)]

# Orleans Configuration Guide

This Configuration Guide explains the key configuration parameters and how they should be used for most typical usage scenarios.

**Orleans Configuration xsd file** is located [here](https://github.com/dotnet/orleans/blob/master/src/Orleans.Core/Configuration/OrleansConfiguration.xsd).

Orleans can be used in a variety of configurations that fit different usage scenarios, such as local single node deployment for development and testing, cluster of servers, multi-instance Azure worker role, etc. All of the different target scenarios are achieved by specifying particular values in the Orleans configuration XML files. This guide provides instructions for the key configuration parameters that are necessary to make Orleans run in one of the target scenarios. There are also other configuration parameters that primarily help fine tune Orleans for better performance. They are documented in the XSD schema and in general are not required even for running the system in production.

 Orleans is a framework for building and running high scale services. A typical deployment of an Orleans application spans a cluster of servers. The instances of the Orleans runtime, called silos, running on each of the servers need to be configured to connect to each other. In addition to that, there is always a client component that connects to the Orleans deployment, most typically a web frontend, that needs to be configured to connect to the silos. The [Server Configuration](Server-Configuration.md) and [Client Configuration](Client-Configuration.md) sections of the guide cover those aspects, respectively. The section on [Typical Configurations](Typical-Configurations.md) provides a summary of a few common configurations.

**Important**: Make sure you properly configure .NET Garbage Collection as detailed in [Configuring .NET Garbage Collection](Configuring-.NET-Garbage-Collection.md).
