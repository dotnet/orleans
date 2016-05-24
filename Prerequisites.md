---
layout: page
title: Prerequisites
---
{% include JB/setup %}


Orleans is a set of .NET libraries. In order to use Orleans, you need [.NET Framework](http://www.microsoft.com/net) 4.5.1 or higher and a copy of [Visual Studio](https://www.visualstudio.com) 2015 or higher. Note that the Express versions of Visual Studio do not support extension packages, but you can use Orleans by adding references to the NuGet packages directly.

In production, Orleans requires persistent storage. The following technologies are supported (only need one of those):

* [Azure](https://azure.microsoft.com/en-us/pricing) - Tested with [Azure SDK](http://azure.microsoft.com/en-us/downloads) 2.4 - 2.8
* [SQL Server](https://www.microsoft.com/en-us/server-cloud/products/sql-server) 2008 or higher
* [ZooKeeper](https://zookeeper.apache.org) 3.4.0 or higher
* [MySQL](https://www.mysql.com) 5.0 or higher
* [Consul](https://www.consul.io) 0.6.0 or higher
