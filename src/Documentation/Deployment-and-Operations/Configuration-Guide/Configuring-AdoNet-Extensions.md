---
layout: page
title: AdoNet extensions
---

# AdoNet Extensions

Any reliable production-style Orleans deployment requires using persistent storage to keep system state, specifically Orleans cluster status and the data used for the reminders functionality. In addition to out of the box support for Azure storage Orleans also provides an option to store this information in AdoNet server.

In order to use AdoNet server for Persistence, Clustering or Reminders, one needs to adjust server-side and client-side configurations.

The server configuration should look like this:

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

The client configuration should look like this:

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

Where the ConnectionString is set to any valid AdoNet Server connection string. 

In order to use AdoNet Server as the state store for persistence, reminders and clustering, there's scripts defining the AdoNet database which you should make sure that all servers that will be hosting Orleans silos can reach the database and has access rights to it! We’ve tripped up a few times on this seemingly trivial concern during our testing.

The scripts will be copied to project directory \OrleansAdoNetContent where each supported ADO.NET extensions has its own directory, after you install or do a nuget restore on the AdoNet extension nugets. We splited AdoNet nugets into per feature nugets:
`Microsoft.Orleans.Clustering.AdoNet` for clustering, `Microsoft.Orleans.Persistence.AdoNet` for persistence and `Microsoft.Orleans.Reminders.AdoNet` for reminders.
