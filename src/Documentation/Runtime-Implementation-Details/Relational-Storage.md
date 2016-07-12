---
layout: page
title: Relational Storage
---

Relational storage backend code in Orleans is built on generic ADO.NET functionality and is consequently database vendor agnostic. The Orleans data storage layout has been explained already in [Runtime Tables](Runtime-Tables.md). Setting up the connection strings are done as explained in [Orleans Configuration Guide ](../Orleans-Configuration-Guide/index.md) and [SQL Tables](http://dotnet.github.io/orleans/Advanced-Concepts/Configuring-SQL-Tables).

To make Orleans code function with a given relational database backend, two things are needed:

1. Appropriate ADO.NET libraries need to be loaded to the process. This should be defined as usual, e.g. via [DbProviderFactories](https://msdn.microsoft.com/en-us/library/dd0w4a2z(v=vs.110).aspx) element in application configuration.
2. Orleans should know about which libraries to use (multiple can exist in GAC or otherwise). The ADO.NET invariant is provided via ``AdoInvariant`` attribute in the element defining the connection string, by default it is `System.Data.SqlClient`
3. The database needs to exist so that it is compatible with the code. This is most conveniently done by creating a vendor specific database creation script. Currently there is database script defined for SQL Server for versions 2000 and newer ([CreateOrleansTables_SqlServer.sql](https://github.com/dotnet/orleans/blob/master/src/OrleansProviders/SQLServer/CreateOrleansTables_SqlServer.sql)). If you need other ones, open an issue or please, stop by at [Orleans Gitter](https://gitter.im/dotnet/orleans?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge).

Orleans uses a fixed set of queries defined in a well-known table ``OrleansQuery``. These are the only queries used by Orleans. The queries are loaded when silo starts using the given connection string. All the queries are also parameterized, i.e. Orleans does not use techniques such as string formatting to run queries. From the perspective of Orleans database code, the code will technically work with any given database structure as long as the following are defined (this is the interface contract between Orleans and relational storage):

1. The names of the queries defined in ``OrleansQuery`` are defined as Orleans expects them. The keys are hardcoded in Orleans database code.
2. The column names and types are what Orleans expects when Orleans issues a ``SELECT`` query.
3. The parameter names and types are what Orleans expects when Orleans issues other than ``SELECT`` query.

Specifically from the aforementioned three rules it follows that table names and layout, indexing and so forth can be defined and tuned without code changes as long as the used names and types are retained. The interface contract is written in more detail in [CreateOrleansTables_SqlServer.sql](https://github.com/dotnet/orleans/blob/master/src/OrleansProviders/SQLServer/CreateOrleansTables_SqlServer.sql), which contains a throughout description of the table structure and how it related to the
[Runtime Tables](Runtime-Tables.md), [Cluster Management](Cluster-Management.md) and the concrete [membership protocol implementation](https://github.com/dotnet/orleans/blob/master/src/Orleans/SystemTargetInterfaces/IMembershipTable.cs). Also, the SQL Server implementation contains SQL Server edition specific tuning.

Currently all but one query are single row inserts or updates (note, one could replace ``UPDATE`` queries with ``INSERT`` ones provided the associated ``SELECT`` queries would provide the last write) except for statistic inserts. Statistic insert, as defined by ``InsertOrleansStatisticsKey`` writes the statistics in batches of predefined maximum size ([currently 200 rows](https://github.com/dotnet/orleans/blob/master/src/OrleansSQLUtils/Storage/SqlStatisticsPublisher.cs#L206)) using ``UNION ALL`` for all databases except for Oracle, for which a ``UNION ALL FROM DUAL`` construct is used. ``InsertOrleansStatisticsKey`` is the only query that defines a kind of a template parameters of which Orleans multiplies as many times as there are parameters with differing values.

## Known issues

Currently there is a database script only for SQL Server. Closely related to this is that the current implementation is only tested with SQL Server. To add a new backend and test it with the current set, the following needs to be done:

1. Add a new script to create the database.
2. Add the vendor invariant name (e.g. *System.Data.SqlClient*) to [AdoNetInvariants](https://github.com/dotnet/orleans/blob/master/src/OrleansSQLUtils/Storage/AdoNetInvariants.cs#L34). This is used to select the correct statistics insert mode (i.e. the ``UNION ALL`` with or without ``FROM DUAL``) and in automatic testing.
3. Add some other ADO.NET provider specific data to [QueryConstantsBag](https://github.com/dotnet/orleans/blob/master/src/OrleansSQLUtils/Storage/QueryConstantsBag.cs#L43). These are (potentially) used in some query operations.
4. Patch the test runner so it accepts the invariant name as a parameter, see at [SqlTestsEnvironment](https://github.com/dotnet/orleans/blob/master/src/Tester/RelationalUtilities/SqlTestsEnvironment.cs#L38) and [LivenessTests_SqlServer](https://github.com/dotnet/orleans/blob/master/src/Tester/MembershipTests/LivenessTests.cs#L423). The relational setup testing code is a good candinate for refactoring. For instance one could detect if a database is installed in listening to connections when it has a script defined and if so, run the storage tests on it.

Currently there is no automatic storage tests to detect correct metrics or statistics operations
