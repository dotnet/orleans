---
layout: page
title: Orleans Configuration Guide
---

# Orleans Configuration Guide

This Configuration Guide explains the key configuration parameters and how they should be used for most typical usage scenarios.

Orleans can be used in a variety of configurations that fit different usage scenarios, such as local single node deployment for development and testing, cluster of servers, multi-instance Azure worker role, etc. 

This guide provides instructions for the key configuration parameters that are necessary to make Orleans run in one of the target scenarios. There are also other configuration parameters that primarily help fine tune Orleans for better performance.

Silos and Clients are configured programmatically via a `SiloHostBuilder` and `ClientBuilder` respectively and a number of supplemental option classes.
Option classes in Orleans follow the [ASP.NET Options](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options/) pattern, and can be loaded via files, environment variables etc.
Please refer to the [Options pattern documentation](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options/) for more information.

If you want to configure a silo and a client for local development, look at the [Local Development Configuration](local_development_configuration.md) section.
The [Server Configuration](server_configuration.md) and [Client Configuration](client_configuration.md) sections of the guide cover configuring silos and clients, respectively. 
 
 The section on [Typical Configurations](typical_configurations.md) provides a summary of a few common configurations.

 A list of important core options that can be configured can be found on [this section](list_of_options_classes.md).

**Important**: Make sure you properly configure .NET Garbage Collection as detailed in [Configuring .NET Garbage Collection](configuring_.NET_garbage_collection.md).
