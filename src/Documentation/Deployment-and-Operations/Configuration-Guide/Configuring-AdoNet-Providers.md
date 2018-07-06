---
layout: page
title: Configuring ADO.NET Providers
---

# Configuring ADO.NET Providers

Any reliable deployment of Orleans requires using persistent storage to keep system state, specifically Orleans cluster membership table and reminders.
One of the available options is using a SQL database via the ADO.NET providers.

In order to use ADO.NET for persistence, clustering or reminders, one needs to configure the ADO.NET providers as part of the silo configuration, and, in case of clustering, also as part of the client configurations.

The silo configuration code should look like this:

``` c#
var siloHostBuilder = new SiloHostBuilder();
var invariant = "System.Data.SqlClient"; // for Microsoft SQL Server
var connectionString = "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=Orleans;Integrated Security=True;Pooling=False;Max Pool Size=200;Asynchronous Processing=True;MultipleActiveResultSets=True";
//use AdoNet for clustering 
siloHostBuilder.UseAdoNetClustering(options =>
            {
                options.Invariant = invariant;
                options.ConnectionString = connectionString;
            });
//use AdoNet for reminder service
siloHostBuilder.UseAdoNetReminderService(options =>
            {
                options.Invariant = invariant;
                options.ConnectionString = connectionString;
            });
//use AdoNet for Persistence
siloHostBuilder.AddAdoNetGrainStorage("GrainStorageForTest", options =>
            {
                options.Invariant = invariant;
                options.ConnectionString = connectionString;
            });
```

The client configuration code should look like this:

``` c#
var siloHostBuilder = new SiloHostBuilder();
var invariant = "System.Data.SqlClient";
var connectionString = "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=Orleans;Integrated Security=True;Pooling=False;Max Pool Size=200;Asynchronous Processing=True;MultipleActiveResultSets=True";
//use AdoNet for clustering 
siloHostBuilder.UseAdoNetClustering(options =>
            {
                options.Invariant = invariant;
                options.ConnectionString = connectionString;
            });
```

Where the `ConnectionString` is set to a valid AdoNet Server connection string. 

In order to use ADO.NET providers for persistence, reminders or clustering, there are scripts for creating database artifacts, to which all servers that will be hosting Orleans silos need to have access.
Lack of access to the target database is a typical mistake we see developers making.

The scripts will be copied to project directory \OrleansAdoNetContent where each supported ADO.NET extensions has its own directory, after you install or do a nuget restore on the AdoNet extension nugets. We splitted AdoNet nugets into per feature nugets:
`Microsoft.Orleans.Clustering.AdoNet` for clustering, `Microsoft.Orleans.Persistence.AdoNet` for persistence and `Microsoft.Orleans.Reminders.AdoNet` for reminders.
