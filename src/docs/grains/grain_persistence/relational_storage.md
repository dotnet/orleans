---
layout: page
title: ADO.NET Grain Persistence
---

# ADO.NET Grain Persistence

Relational storage backend code in Orleans is built on generic ADO.NET functionality and is consequently database vendor agnostic.
The Orleans data storage layout has been explained already in Runtime Tables.
Setting up the connection strings are done as explained in [Orleans Configuration Guide](~/docs/clusters_and_clients/configuration_guide/index.md).

To make Orleans code function with a given relational database backend, the following is required:

1. Appropriate ADO.NET library must be loaded to the process. This should be defined as usual, e.g. via [DbProviderFactories](https://docs.microsoft.com/en-us/dotnet/framework/data/adonet/obtaining-a-dbproviderfactory) element in application configuration.
2. Configure the ADO.NET invariant via `Invariant` property in the options.
3. The database needs to exist and be compatible with the code. This is done by running a vendor specific database creation script. For more information, see [ADO.NET Configuration](~/docs/clusters_and_clients/configuration_guide/adonet_configuration.md).

The ADO .NET grain storage provider allows you to store grain state in relational databases.
Currently following databases are supported:

* SQL Server
* MySQL/MariaDB
* PostgreSQL
* Oracle

First, install the base package:

``` powershell
Install-Package Microsoft.Orleans.Persistence.AdoNet
```

Read the [ADO.NET Configuration](~/docs/clusters_and_clients/configuration_guide/adonet_configuration.md) article for information on configuring your database, including the corresponding ADO.NET Invariant and setup scripts.

The following is an example of how to configure an ADO.NET storage provider via `ISiloHostBuilder`:

```csharp
var siloHostBuilder = new SiloHostBuilder()
    .AddAdoNetGrainStorage("OrleansStorage", options =>
    {
        options.Invariant = "<Invariant>";
        options.ConnectionString = "<ConnectionString>";
        options.UseJsonFormat = true;
    });
```

Essentially, you only need to set the database-vendor-specific connection string and an
`Invariant` (see [ADO.NET Configuration](~/docs/clusters_and_clients/configuration_guide/adonet_configuration.md)) that identifies the vendor. You may also choose the format in which the data
is saved, which may be either binary (default), JSON, or XML. While binary is the most compact
option, it is opaque and you will not be able to read or work with the data. JSON is the
recommended option.

You can set the following properties via `AdoNetGrainStorageOptions`:

```csharp
/// <summary>
/// Options for AdoNetGrainStorage
/// </summary>
public class AdoNetGrainStorageOptions
{
    /// <summary>
    /// Connection string for AdoNet storage.
    /// </summary>
    [Redact]
    public string ConnectionString { get; set; }

    /// <summary>
    /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialized prior to use.
    /// </summary>
    public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
    /// <summary>
    /// Default init stage in silo lifecycle.
    /// </summary>
    public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

    /// <summary>
    /// The default ADO.NET invariant used for storage if none is given.
    /// </summary>
    public const string DEFAULT_ADONET_INVARIANT = AdoNetInvariants.InvariantNameSqlServer;
    /// <summary>
    /// The invariant name for storage.
    /// </summary>
    public string Invariant { get; set; } = DEFAULT_ADONET_INVARIANT;

    /// <summary>
    /// Whether storage string payload should be formatted in JSON.
    /// <remarks>If neither <see cref="UseJsonFormat"/> nor <see cref="UseXmlFormat"/> is set to true, then BinaryFormatSerializer will be configured to format storage string payload.</remarks>
    /// </summary>
    public bool UseJsonFormat { get; set; }
    public bool UseFullAssemblyNames { get; set; }
    public bool IndentJson { get; set; }
    public TypeNameHandling? TypeNameHandling { get; set; }

    public Action<JsonSerializerSettings> ConfigureJsonSerializerSettings { get; set; }

    /// <summary>
    /// Whether storage string payload should be formatted in Xml.
    /// <remarks>If neither <see cref="UseJsonFormat"/> nor <see cref="UseXmlFormat"/> is set to true, then BinaryFormatSerializer will be configured to format storage string payload.</remarks>
    /// </summary>
    public bool UseXmlFormat { get; set; }
}
```

The ADO.NET persistence has functionality to version data and define arbitrary (de)serializers with arbitrary application rules and streaming, but currently there is no method to expose them to application code.

## ADO.NET Persistence Rationale

The principles for ADO.NET backed persistence storage are:

1. Keep business critical data safe an accessible while data, the format of data and code evolve.
2. Take advantage of vendor and storage specific functionality.

In practice this means adhering to ADO.NET implementation goals and some added implementation logic in ADO.NET specific storage provider that allow evolving the shape of the data in the storage.

In addition to the usual storage provider capabilities, the ADO.NET provider has built-in capability to

1. Change storage data format from one format to another format (e.g. from JSON to binary) when round-tripping state.
2. Shape the type to be saved or read from the storage in arbitrary ways. This helps to evolve the version state.
3. Stream data out of the database.

Both `1.` and `2.` can be applied on arbitrary decision parameters, such as *grain ID*, *grain type*, *payload data*.

This happen so that one chooses a format, e.g. [Simple Binary Encoding (SBE)](https://github.com/real-logic/simple-binary-encoding) and implements
[IStorageDeserializer](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/Storage/Provider/IStorageDeserializer.cs) and [IStorageSerializer](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/Storage/Provider/IStorageSerializer.cs).
The built-in serializers have been built using this method. The [OrleansStorageDefault(De)Serializer](https://github.com/dotnet/orleans/tree/master/src/AdoNet/Orleans.Persistence.AdoNet/Storage/Provider) can be used as examples on how to implement other formats.

When the serializers have been implemented, they need to be added to the `StorageSerializationPicker` property in [AdoNetGrainStorage](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/Storage/Provider/AdoNetGrainStorage.cs).
This is an implementation of [IStorageSerializationPicker](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/Storage/Provider/IStorageSerializationPicker.cs). By default
[StorageSerializationPicker](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/Storage/Provider/StorageSerializationPicker.cs) will be used. And example of changing data storage format
or using serializers can be seen at [RelationalStorageTests](https://github.com/dotnet/orleans/blob/master/test/Extensions/TesterAdoNet/StorageTests/Relational/RelationalStorageTests.cs).

Currently there is no method to expose this to Orleans application consumption as there is no method to access the framework created [AdoNetGrainStorage](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/Storage/Provider/AdoNetGrainStorage.cs).

## Goals of the design

### 1. Allow use of any backend that has an ADO.NET provider

This should cover the broadest possible set of backends available for .NET, which is a factor in on-premises installations. Some providers are listed at [ADO.NET Data Providers MSDN page](https://docs.microsoft.com/en-us/dotnet/framework/data/adonet/ado-net-overview),
but for the sake of a remark, not all are listed, such as [Teradata](https://downloads.teradata.com/download/connectivity/net-data-provider-for-teradata).

### 2. Maintain the potential to tune queries and database structure as appropriate, even while a deployment is running

In many cases, the servers and databases are hosted by a third party in contractual relation with the client. It is not an unusual
situation the hosting environment is virtualized and performance fluctuates due to unforeseen factors, such as noisy neighbors or faulty hardware. It may
not be possible to alter and re-deploy either Orleans binaries (contractual reasons) or even application binaries, but usually it is possible to tweak the
database deployment. Altering *standard components*, such as Orleans binaries, requires a lengthier procedure as to what is afforded in a given situation.

### 3. Allow one to make use of vendor and version specific abilities

Vendors have implemented different extensions and features within their products. It is sensible to make use of these features when they are available.
These features are such as [native UPSERT](https://www.postgresql.org/about/news/1636/) or [PipelineDB](https://www.pipelinedb.com/) in PostgreSQL,
[PolyBase](https://docs.microsoft.com/en-us/sql/relational-databases/polybase/get-started-with-polybase) or [natively compiled tables and stored procedures](https://docs.microsoft.com/en-us/sql/relational-databases/in-memory-oltp/native-compilation-of-tables-and-stored-procedures) in SQL Server
&ndash; and myriads of other features.

### 4. Make it possible to optimize hardware resources

When designing an application, it is often possible to anticipate which data needs to be inserted faster than other data and
which data could be more likely put into *cold storage* which is cheaper (e.g. splitting data between SSD and HDD). As for an example,
further considerations are that the physical location of some data could be more expensive (e.g. SSD RAID viz HDD RAID), more secured
or some other decision attribute used. Related to *point 3.* Some databases offer special partitioning schemes, such as SQL Server [Partitioned Tables and Indexes](https://docs.microsoft.com/en-us/sql/relational-databases/partitions/partitioned-tables-and-indexes).

This principle applies also throughout the application life-cycle. Considering one of the principles of Orleans itself is a high-availability system,
it should be possible to adjust storage system without interruption to Orleans deployment or that it should be possible to adjust the queries according
to data and other application parameters. One example of changes is in Brian Harry's [blog post](https://blogs.msdn.microsoft.com/bharry/2016/02/06/a-bit-more-on-the-feb-3-and-4-incidents/):

> When a table is small, it almost doesn’t matter what the query plan is. When it’s medium an OK query plan is fine. When it's huge (millions upon millions or billions of rows) a tiny, slight variation in query plan can kill you. So, we hint our sensitive queries heavily.

### 5. No assumptions on what tools, libraries or deployment processes are used in organizations

Many organizations have familiarity with a certain set of database tools, examples being [Dacpac](https://docs.microsoft.com/en-us/sql/relational-databases/data-tier-applications/data-tier-applications)
or [Red Gate](https://www.red-gate.com/). It may be so that deploying a database requires either a permission or a person, such as someone
in a DBA role, to do it. Usually this means also having the target database layout and a rough sketch of the queries the application will
produce to the database to be used estimate the load. There might be processes, perhaps influenced by industry standards, which mandate script based deployment.
Having the queries and database structures in an external script makes this possible.

### 6. Use the minimum set needed of interface functionality to load the ADO.NET libraries and functionality

This is both fast and has less surface to be exposed to ADO.NET library implementation discrepancies.

### 7. Make the design shardable

When it makes sense, for instance in relational storage provider, make the design readily shardable. This means for instance no database dependent
data (e.g. `IDENTITY`) and basically it means the information that distinguishes row data should build on only data from the actual parameters.

### 8. Make the design easy to test

Creating a new backend should be ideally as easy as translating one of the deployment scripts and adding a new connection string to tests assuming default
parameters, check if a given database is installed and then run the tests against it.

### 9. Taking into account the previous points, make both porting scripts for new backends and modifying already deployed backend scripts as transparent as possible

## Realization of the goals

Orleans framework does not have knowledge of deployment specific hardware (which may change during active deployment), the change of data during the deployment life-cycle and some vendor specific features are usable in only some situations. For this reason, the interface between relational database and Orleans should adhere a minimum set of abstractions and rules to meet the goals but to make it also robust against misuse and easy to test if needed.
Runtime Tables, Cluster Management and the concrete [membership protocol implementation](https://github.com/dotnet/orleans/blob/master/src/Orleans/SystemTargetInterfaces/IMembershipTable.cs). Also, the SQL Server implementation contains SQL Server edition specific tuning.
The interface contract between the database and Orleans is defined as follows:

1. The general idea is that data is read and written through Orleans specific queries.
   Orleans operates on column names and types when reading and on parameter names and types when writing.
2. The implementations **must** preserve input and output names and types. Orleans uses these parameters to reads query results by name and type.
   Vendor and deployment specific tuning is allowed and contributions are encouraged as long as the interface contract is maintained.	 
3. The implementation across vendor specific scripts **should** preserve the constraint names.
   This simplifies troubleshooting by virtue of uniform naming across concrete implementations.
4. **Version** &ndash; or **ETag** in application code &ndash; for Orleans represents a unique version.
   The type of its actual implementation is not important as long as it represents a unique version. In the implementation, Orleans code expects a signed 32-bit integer.
5. For the sake of being explicit and removing ambiguity, Orleans expects some queries to return either **TRUE as > 0** value
   or **FALSE as = 0** value. That is, affected rows or such does not matter. If an error is raised or an exception is thrown
   the query **must** ensure the entire transaction is rolled back and may either return FALSE or propagate the exception.
6. Currently all but one query are single row inserts or updates (note, one could replace ``UPDATE`` queries with ``INSERT`` ones provided the associated
   ``SELECT`` queries would provide the last write) except for statistic inserts. Statistic insert, as defined by ``InsertOrleansStatisticsKey`` writes the statistics in batches of predefined maximum size using ``UNION ALL`` for all databases except for Oracle, for which a ``UNION ALL FROM DUAL`` construct is used. ``InsertOrleansStatisticsKey`` is the only query that defines a kind of a template parameters of which Orleans multiplies as many times as there are parameters with differing values.

Database engines support in-database programming, this is is similar to an idea of loading an executable script and invoke it to execute database operations. In pseudocode it could be depicted as

```csharp
const int Param1 = 1;
const DateTime Param2 = DateTime.UtcNow;
const string queryFromOrleansQueryTableWithSomeKey = "SELECT column1, column2 FROM <some Orleans table> where column1 = @param1 AND column2 = @param2;";
TExpected queryResult = SpecificQuery12InOrleans<TExpected>(query, Param1, Param2);
```

These principles are also [included in the database scripts](~/docs/clusters_and_clients/configuration_guide/adonet_configuration.md).

## Some ideas on applying customized scripts

1. Alter scripts in `OrleansQuery` for grain persistence with `IF ELSE`
   so that some state is saved using the default `INSERT` while some grain state uses, for instance, [memory optimized tables](https://docs.microsoft.com/en-us/sql/relational-databases/in-memory-oltp/memory-optimized-tables).
   The `SELECT` queries need to be altered accordingly.
2. The idea in `1.` can be used to take advantage of other deployment or vendor specific aspects. Such as splitting data between `SSD` or `HDD`, putting some data on encrypted tables,
   or perhaps inserting statistics data via SQL Server to Hadoop or even [linked servers](https://docs.microsoft.com/en-us/sql/relational-databases/linked-servers/linked-servers-database-engine).

The altered scripts can be tested running the Orleans test suite or straight in the database using, for instance, [SQL Server Unit Test Project](https://msdn.microsoft.com/en-us/library/jj851212.aspx).

## Guidelines for adding new ADO.NET providers

1. Add a new database setup script according to the [Realization of the goals](#realization-of-the-goals) section above.
2. Add the vendor ADO invariant name to [AdoNetInvariants](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Shared/Storage/AdoNetInvariants.cs#L34) and ADO.NET provider specific data to [DbConstantsStore](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Shared/Storage/DbConstantsStore.cs). These are (potentially) used in some query operations. e.g. to select the correct statistics insert mode (i.e. the ``UNION ALL`` with or without ``FROM DUAL``).
3. Orleans has comprehensive tests for all system stores: membership, reminders and statistics. Adding tests for the new database script is done by copy-pasting existing test classes and changing the ADO invariant name. Also, derive from [RelationalStorageForTesting](https://github.com/dotnet/orleans/blob/master/test/Extensions/TesterAdoNet/RelationalUtilities/RelationalStorageForTesting.cs) in order to define test functionality for the ADO invariant.
