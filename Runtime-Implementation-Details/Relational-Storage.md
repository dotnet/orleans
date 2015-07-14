---
layout: page
title: Relational Storage
---

Relational storage backend code in Orleans is built on generic ADO.NET functionality and is consequently database vendor agnostic. The Orleans data storage layout has been explained already in [Runtime Tables](Runtime-Tables). Setting up the connection strings are done as explained in [Orleans Configuration Guide ](http://dotnet.github.io/orleans/Orleans-Configuration-Guide/) and [SQL Tables](http://dotnet.github.io/orleans/Advanced-Concepts/Configuring-SQL-Tables).

To make Orleans code function with a given relational database backend, two things are needed:

1. Appropriate ADO.NET libraries need to be loaded to the process. This should be defined as usual, e.g. via [DbProviderFactories](https://msdn.microsoft.com/en-us/library/dd0w4a2z(v=vs.110).aspx) element in application configuration.
2. The database needs to exist so that it is compatible with the code. This is most conveniently done by creating a vendor specific database creation script. Currently there is database script defined for SQL Server for versions 2000 and newer ([CreateOrleansTables_SqlServer.sql](https://github.com/dotnet/orleans/blob/master/src/OrleansProviders/SQLServer/CreateOrleansTables_SqlServer.sql)).

Orleans uses a fixed set of queries defined in a well-known table ``OrleansQuery``. These are the only queries used by Orleans. The queries are loaded when silo starts using the given connection string. All the queries are also parameterized, i.e. Orleans does not use techniques such as string formatting to run queries. From the perspective of Orleans database code, the code will technically work with any given database structure as long as the following are defined (this is the interface contract between Orleans and relational storage):

1. The names of the queries defined in ``OrleansQuery`` are defined as Orleans expects them. The keys are hardcoded in Orleans database code.
2. The column names and types are what Orleans expects when Orleans issues a ``SELECT`` query.
3. The parameter names and types are what Orleans expects when Orleans issues other than ``SELECT`` query.

Specifically from the aforementioned three rules it follows that table names and layout, indexing and so forth can be defined and tuned without code changes as long as the used names and types are retained. The interface contract is written in more detail in [CreateOrleansTables_SqlServer.sql](https://github.com/dotnet/orleans/blob/master/src/OrleansProviders/SQLServer/CreateOrleansTables_SqlServer.sql), which contains a throughout description of the table structure and how it related to the
[Runtime Tables](Runtime-Tables), [Cluster Management](Cluster-Management) and the concrete [membership protocol implementation](https://github.com/dotnet/orleans/blob/master/src/Orleans/SystemTargetInterfaces/IMembershipTable.cs). Also, the SQL Server implementation contains SQL Server edition specific tuning.

Currently all but one query are single row inserts or updates (note, one could replace ``UPDATE`` queries with ``INSERT`` ones provided the associated ``SELECT`` queries would provide the last write) except for statistic inserts. Statistic insert, as defined by ``InsertOrleansStatisticsKey`` writes the statistics in batches of predefined maximum size ([currently 200 rows](https://github.com/dotnet/orleans/blob/master/src/OrleansProviders/SQLServer/SqlStatisticsPublisher.cs#L154)) using ``UNION ALL`` for all databases except for Oracle, for which a ``UNION ALL FROM DUAL`` construct is used. ``InsertOrleansStatisticsKey`` is the only query that defines a kind of a template parameters of which Orleans multiplies as many times as there are parameters with differing values.

## Known issues

###### Relational backend is implemented currently on for SQL Server

Currently there is a database script only for SQL Server. Closely related to this is that the current implementation is only tested with SQL Server. To add a new backend and test it with the current set, the following needs to be done:

1. Add a new script to create the database.
2. Add the vendor invariant name (e.g. *System.Data.SqlClient*) to [WellKnownRelationalInvariants](https://github.com/dotnet/orleans/blob/master/src/Orleans/RelationalStorage/RelationalConstants.cs#L38). This is used to select the correct statistics insert mode (i.e. the ``UNION ALL`` with or without ``FROM DUAL``) and in automatic testing.
3. Add some queries to [RelationalConstants](https://github.com/dotnet/orleans/blob/master/src/Orleans/RelationalStorage/RelationalConstants.cs#L160). These are used to create and re-create the database automatically in tests.
4. Add the test to be run by the test suite. The relevant place is [SQLMembershipTableTests](https://github.com/dotnet/orleans/blob/master/src/TesterInternal/MembershipTests/SQLMembershipTableTests.cs) and [LivenessTests_SqlServer](https://github.com/dotnet/orleans/blob/master/src/TesterInternal/MembershipTests/LivenessTests.cs#L383). The relational setup testing code is a good candinate for refactoring. For instance one could detect if a database is installed in listening to connections when it has a script defined and if so, run the storage tests on it.

###### Currently there is no automatic storage tests to detect correct metrics, statistics or reminder operations

###### Currently the database vendor name invariant is not transmitted to Orleans relational backend
This should be a minor issue, recorded at [Passing ADO.NET provider invariant name to RelationalStorage #569](https://github.com/dotnet/orleans/issues/569).
