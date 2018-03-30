---
layout: page
title: Prerequisites
---

[!include[](../../warning-banner.md)]

# Prerequisites

Orleans is a set of .NET libraries delivered via [NuGet packages](NuGets.md).
In order to use Orleans, you need [.NET Framework](http://dot.net) 4.6.1 (since 1.5.0, 4.5.1 for prior versions) or higher and a copy of [Visual Studio](https://www.visualstudio.com) 2015 or higher.
Note that the Express versions of Visual Studio do not support extension packages, but you can use Orleans by adding references to the NuGet packages directly.

In production, Orleans requires persistent storage for reliable cluster membership.
The following storage technologies are supported for managing cluster membership state (only need one of those):

* [Azure Table Storage](https://azure.microsoft.com/en-us/services/storage/tables/) - Tested with [Azure SDK](http://azure.microsoft.com/en-us/downloads) 2.4 - 2.8
* [SQL Server](https://www.microsoft.com/en-us/server-cloud/products/sql-server) 2008 or higher
* [ZooKeeper](https://zookeeper.apache.org) 3.4.0 or higher
* [MySQL](https://www.mysql.com) 5.0 or higher
* [PostgreSQL](https://postgresql.org/) 9.5 or higher
* [Consul](https://www.consul.io) 0.6.0 or higher
* [DynamoDB](https://aws.amazon.com/dynamodb/) - Tested with [AWSSDK - Amazon DynamoDB 3.1.5.3](https://www.nuget.org/packages/AWSSDK.DynamoDBv2/3.1.5.3)

Another production deployment option is to use [Azure Service Fabric](https://azure.microsoft.com/en-us/services/service-fabric/).
There is a [NuGet package](https://www.nuget.org/packages/Microsoft.Orleans.ServiceFabric/) that helps with that. It has a dependency on Service Fabric 2.1.163.
