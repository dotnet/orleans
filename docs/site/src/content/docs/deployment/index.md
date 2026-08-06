---
title: Run an Orleans application
description: Learn how to run an Orleans app in .NET.
ms.date: 05/23/2025
ms.topic: overview
---

# Run an Orleans application

A typical Orleans application consists of a cluster of server processes (silos) where grains live, and a set of client processes (usually web servers) receiving external requests, turning them into grain method calls, and returning results. Therefore, the first step to run an Orleans application is starting a cluster of silos. For testing purposes, a cluster can consist of a single silo. For a reliable production deployment, more than one silo in a cluster is desirable for fault tolerance and scale.

Once the cluster runs, start one or more client processes that connect to the cluster and can send requests to the grains. Clients connect to a special TCP endpoint on silos called a gateway. By default, every silo in a cluster has a client gateway enabled. Clients connect to all silos in parallel for better performance and resilience.

## Configure and start a silo

Configure the silo in conjunction with an <xref:Microsoft.Extensions.Hosting.IHost>. For more information, see [Orleans: Server configuration](../host/configuration-guide/server-configuration.md). After configuring the silo within the host, start the host to initiate the Orleans silo.

## Configure and connect a client

Configure clients similarly to silos, using an `IHost`. For more information, see [Orleans: Client configuration](../host/configuration-guide/client-configuration.md). When the client is configured, start the host instance to have the client connect to the silos.

## Production configurations

The configuration examples used here are for testing silos and clients running on the same machine (`localhost`). In production, silos and clients usually run on different servers and are configured with one of the reliable cluster configuration options. Find more information about this in the [Configuration guide](../host/configuration-guide/index.md) and the description of [Cluster management](../implementation/cluster-management.md).

## Next steps

> [!div class="nextstepaction"]
> [Deploy Orleans to Azure App Service](deploy-to-azure-app-service.md)
