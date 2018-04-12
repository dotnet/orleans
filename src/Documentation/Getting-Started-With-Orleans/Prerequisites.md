---
layout: page
title: Prerequisites
---

# Prerequisites

Orleans is a set of .NET libraries delivered via [NuGet packages](NuGets.md).
In order to use Orleans, you need either [.NET Core](https://docs.microsoft.com/en-us/dotnet/core/index) 2.0 or higher of [Full .NET Framework](https://www.microsoft.com/net/download/Windows/run) 4.6.1 or higher.
.NET Core enables Orleans to run on both Windows and non-Windows platforms, such as Linux and MacOS.
At this point, Orleans is still primarily tested on Windows.
However, multiple customers successfully run Orleans 2.0 on non-Windows platforms as well.

For development, it is recommended to use [Visual Studio](https://www.visualstudio.com) 2017 or higher. But you can also use other development tools of your choice. It is a requirement to use/upgrade to new csproj format for Orleans 2.0 to function correctly. If your application is still using the legacy csproj format, please upgrade it as the first step of migrating to Orleans 2.0. [This blog](https://www.natemcmaster.com/blog/2017/03/09/vs2015-to-vs2017-upgrade/) is a good and precise start on how to migrate to the new csproj format. 

In production, Orleans requires persistent storage for reliable cluster membership.
The following storage technologies are supported for managing cluster membership state (you only need one of those):

* [Azure Table Storage](https://azure.microsoft.com/en-us/services/storage/tables/) 8.2.1 or higher
* [SQL Server](https://www.microsoft.com/en-us/server-cloud/products/sql-server) 2008 or higher
* [ZooKeeper](https://zookeeper.apache.org) 3.4.0 or higher
* [MySQL](https://www.mysql.com) 5.0 or higher
* [PostgreSQL](https://postgresql.org/) 9.5 or higher
* [Consul](https://www.consul.io) 0.7.0 or higher
* [DynamoDB](https://aws.amazon.com/dynamodb/) - Tested with [AWS SDK - Amazon DynamoDB 3.3.4.17](https://www.nuget.org/packages/AWSSDK.DynamoDBv2/3.3.4.17)

Another production deployment option is to use [Azure Service Fabric](https://azure.microsoft.com/en-us/services/service-fabric/).
There is a [NuGet package](https://www.nuget.org/packages/Microsoft.Orleans.Hosting.ServiceFabric/) that helps with that. It has a dependency on Service Fabric 3.0.472.
