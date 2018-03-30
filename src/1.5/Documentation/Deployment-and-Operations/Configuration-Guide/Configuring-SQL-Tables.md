---
layout: page
title: SQL Tables
---

[!include[](../../../warning-banner.md)]

# SQL System Storage

Any reliable production-style Orleans deployment requires using persistent storage to keep system state, specifically Orleans cluster status and the data used for the reminders functionality. In addition to out of the box support for Azure storage Orleans also provides an option to store this information in SQL server.

In order to use SQL server for the system store, one needs to adjust server-side and client-side configurations.

The server configuration should look like this:

``` xml
<OrleansConfiguration xmlns="urn:orleans">
  <Globals>
      <SystemStore SystemStoreType ="SqlServer"
                 DeploymentId="OrleansTest"
                 DataConnectionString="Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=Orleans;Integrated Security=True;Pooling=False;Max Pool Size=200;Asynchronous Processing=True;MultipleActiveResultSets=True" AdoInvariant="System.Data.SqlClient" />
  </Globals>
</OrleansConfiguration>
```

The client configuration should look like this:

``` xml
<ClientConfiguration xmlns="urn:orleans">
      <SystemStore SystemStoreType ="SqlServer"
                 DeploymentId="OrleansTest"
                 DataConnectionString="Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=Orleans;Integrated Security=True;Pooling=False;Max Pool Size=200;Asynchronous Processing=True;MultipleActiveResultSets=True" AdoInvariant="System.Data.SqlClient" />
</ClientConfiguration>
```

Where the DataConnectionString is set to any valid SQL Server connection string. In order to use SQL Server as the store for system data, there’s now a script file [CreateOrleansTables_*.sql](https://github.com/dotnet/orleans/tree/v1.5.3/src/OrleansSQLUtils)(where asterisk denotes database vendor) in the Binaries\OrleansServer folder which establishes the necessary database objects. Make sure that all servers that will be hosting Orleans silos can reach the database and has access rights to it! We’ve tripped up a few times on this seemingly trivial concern during our testing.
Note that in Orleans 2.0.0 those SQL scripts have been split into per-feature pieces to match the finer grain provider model: [Clustering](https://github.com/dotnet/orleans/tree/v2.0.0-beta3/src/AdoNet/Orleans.Clustering.AdoNet), [Persistence](https://github.com/dotnet/orleans/tree/v2.0.0-beta3/src/AdoNet/Orleans.Persistence.AdoNet), [Reminders](https://github.com/dotnet/orleans/tree/v2.0.0-beta3/src/AdoNet/Orleans.Reminders.AdoNet), and [Statistics](https://github.com/dotnet/orleans/tree/v2.0.0-beta3/src/AdoNet/Orleans.Statistics.AdoNet).

### SQL Metrics and Statistics tables

System tables can currently only be stored in Azure table or SQL server.
For Metrics and Statistics tables however, we provide a generic support to host it in any persistent storage. This is provided via the notion of a `StatisticsProvider`. Any application can write an arbitrary provider to store statistics and metrics data in a persistent store of their choice. Orleans provides an implemention of one such provider: SQL Table Statistics Provider.

In order to use SQL server for statistics and metrics tables, one needs to adjust server-side and client-side configurations.

The server configuration should look like this:

``` xml
<OrleansConfiguration xmlns="urn:orleans">
     <Globals>
         <StatisticsProviders>
             <Provider Type="Orleans.Providers.SqlServer.SqlStatisticsPublisher" Name="MySQLStatsProvider" ConnectionString="Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=Orleans;Integrated Security=True;Pooling=False;Max Pool Size=200;Asynchronous Processing=True;MultipleActiveResultSets=True" />
         </StatisticsProviders>
     </Globals>
     <Defaults>
         <Statistics ProviderType="MySQLStatsProvider" WriteLogStatisticsToTable="true"/>
     </Defaults>
</OrleansConfiguration>
```

The client configuration should look like this:

``` xml
<ClientConfiguration xmlns="urn:orleans">
      <StatisticsProviders>
         <Provider Type="Orleans.Providers.SqlServer.SqlStatisticsPublisher" Name="SQL" ConnectionString="Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=Orleans;Integrated Security=True;Pooling=False;Max Pool Size=200;Asynchronous Processing=True;MultipleActiveResultSets=True" />
      </StatisticsProviders>
      <Statistics ProviderType="MySQLStatsProvider" WriteLogStatisticsToTable="true"/>
</ClientConfiguration>
```
