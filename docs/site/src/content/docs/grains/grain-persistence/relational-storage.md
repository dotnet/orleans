---
title: ADO.NET grain persistence
description: Learn about ADO.NET grain persistence in .NET Orleans.
ms.date: 02/06/2026
ms.topic: how-to
ms.custom: sfi-ropc-nochange
ai-usage: ai-assisted
zone_pivot_groups: orleans-version
---

# ADO.NET grain persistence

Relational storage backend code in Orleans builds on generic ADO.NET functionality and is consequently database vendor agnostic. Set up connection strings as explained in the [Orleans Configuration Guide](../../host/configuration-guide/index.md).

To make Orleans code function with a given relational database backend, you need the following:

1. Load the appropriate ADO.NET library into the process. Define this as usual, for example, via the [DbProviderFactories](../../../framework/data/adonet/obtaining-a-dbproviderfactory.md) element in the application configuration.
1. Configure the ADO.NET invariant via the `Invariant` property in the options.
1. Ensure the database exists and is compatible with the code. Do this by running a vendor-specific database creation script. For more information, see [ADO.NET Configuration](../../host/configuration-guide/adonet-configuration.md).

The ADO.NET grain storage provider allows you to store grain state in relational databases. Currently, the following databases are supported:

- SQL Server
- MySQL/MariaDB
- PostgreSQL
- Oracle

First, install the base package:

```powershell
Install-Package Microsoft.Orleans.Persistence.AdoNet
```

Read the [ADO.NET configuration](../../host/configuration-guide/adonet-configuration.md) article for information on configuring your database, including the corresponding ADO.NET Invariant and setup scripts.

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

The following example shows how to configure an ADO.NET storage provider via the silo builder:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleans(siloBuilder =>
{
    siloBuilder.AddAdoNetGrainStorage("OrleansStorage", options =>
    {
        options.Invariant = "<Invariant>";
        options.ConnectionString = "<ConnectionString>";
    });
});

using var host = builder.Build();
await host.RunAsync();
```

Essentially, you only need to set the database-vendor-specific connection string and an `Invariant` (see [ADO.NET Configuration](../../host/configuration-guide/adonet-configuration.md)) identifying the vendor.

## Configure serialization

Grain storage serialization is configured using the <xref:Orleans.Storage.IGrainStorageSerializer> interface. By default, grain state serializes using `Newtonsoft.Json`. You can customize the serializer by setting the <xref:Orleans.Configuration.AdoNetGrainStorageOptions.GrainStorageSerializer> property.

The following example demonstrates configuring a custom serializer. Your custom serializer (for example, `MyCustomSerializer`) must implement <xref:Orleans.Storage.IGrainStorageSerializer> and be registered in the dependency injection container:

```csharp
// Register your custom serializer in DI
siloBuilder.Services.AddSingleton<IGrainStorageSerializer, MyCustomSerializer>();

// Configure the storage provider to use your serializer
siloBuilder.AddAdoNetGrainStorage(
    "OrleansStorage",
    (OptionsBuilder<AdoNetGrainStorageOptions> optionsBuilder) =>
    {
        optionsBuilder.Configure<IGrainStorageSerializer>(
            (options, serializer) =>
            {
                options.Invariant = "<Invariant>";
                options.ConnectionString = "<ConnectionString>";
                options.GrainStorageSerializer = serializer;
            });
    });
```

For more information on configuring grain storage serializers, see [Grain storage serializers](../../host/configuration-guide/serialization.md#grain-storage-serializers).

You can set the following properties via <xref:Orleans.Configuration.AdoNetGrainStorageOptions>:

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
    /// Stage of silo lifecycle where storage should be initialized.
    /// Storage must be initialized prior to use.
    /// </summary>
    public int InitStage { get; set; } = DEFAULT_INIT_STAGE;

    /// <summary>
    /// Default init stage in silo lifecycle.
    /// </summary>
    public const int DEFAULT_INIT_STAGE =
        ServiceLifecycleStage.ApplicationServices;

    /// <summary>
    /// The default ADO.NET invariant used for storage if none is given.
    /// </summary>
    public const string DEFAULT_ADONET_INVARIANT =
        AdoNetInvariants.InvariantNameSqlServer;

    /// <summary>
    /// The invariant name for storage.
    /// </summary>
    public string Invariant { get; set; } =
        DEFAULT_ADONET_INVARIANT;

    /// <summary>
    /// The grain storage serializer.
    /// </summary>
    public IGrainStorageSerializer GrainStorageSerializer { get; set; }

    /// <summary>
    /// Gets or sets the hasher picker to use for this storage provider.
    /// </summary>
    public IStorageHasherPicker HashPicker { get; set; }
}
```

:::zone-end
:::zone target="docs" pivot="orleans-3-x"

The following example shows how to configure an ADO.NET storage provider via the silo builder:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleans(siloBuilder =>
{
    siloBuilder.AddAdoNetGrainStorage("OrleansStorage", options =>
    {
        options.Invariant = "<Invariant>";
        options.ConnectionString = "<ConnectionString>";
        options.UseJsonFormat = true;
    });
});

using var host = builder.Build();
await host.RunAsync();
```

Essentially, you only need to set the database-vendor-specific connection string and an `Invariant` (see [ADO.NET Configuration](../../host/configuration-guide/adonet-configuration.md)) identifying the vendor. You can also choose the format for saving data: binary (default), JSON, or XML. While binary is the most compact option, it's opaque, and you won't be able to read or work with the data directly. JSON is the recommended option.

You can set the following properties via <xref:Orleans.Configuration.AdoNetGrainStorageOptions>:

```csharp
/// <summary>
/// Options for AdoNetGrainStorage
/// </summary>
public class AdoNetGrainStorageOptions
{
    /// <summary>
    /// Define the property of the connection string
    /// for AdoNet storage.
    /// </summary>
    [Redact]
    public string ConnectionString { get; set; }

    /// <summary>
    /// Set the stage of the silo lifecycle where storage should
    /// be initialized.  Storage must be initialized prior to use.
    /// </summary>
    public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
    /// <summary>
    /// Default init stage in silo lifecycle.
    /// </summary>
    public const int DEFAULT_INIT_STAGE =
        ServiceLifecycleStage.ApplicationServices;

    /// <summary>
    /// The default ADO.NET invariant will be used for
    /// storage if none is given.
    /// </summary>
    public const string DEFAULT_ADONET_INVARIANT =
        AdoNetInvariants.InvariantNameSqlServer;

    /// <summary>
    /// Define the invariant name for storage.
    /// </summary>
    public string Invariant { get; set; } =
        DEFAULT_ADONET_INVARIANT;

    /// <summary>
    /// Determine whether the storage string payload should be formatted in JSON.
    /// <remarks>If neither <see cref="UseJsonFormat"/> nor <see cref="UseXmlFormat"/> is set to true, then BinaryFormatSerializer will be configured to format the storage string payload.</remarks>
    /// </summary>
    public bool UseJsonFormat { get; set; }
    public bool UseFullAssemblyNames { get; set; }
    public bool IndentJson { get; set; }
    public TypeNameHandling? TypeNameHandling { get; set; }

    public Action<JsonSerializerSettings> ConfigureJsonSerializerSettings { get; set; }

    /// <summary>
    /// Determine whether storage string payload should be formatted in Xml.
    /// <remarks>If neither <see cref="UseJsonFormat"/> nor <see cref="UseXmlFormat"/> is set to true, then BinaryFormatSerializer will be configured to format storage string payload.</remarks>
    /// </summary>
    public bool UseXmlFormat { get; set; }
}
```

The ADO.NET persistence provider can version data and define arbitrary (de)serializers with custom application rules and streaming, but currently, there's no method to expose this functionality directly to application code.

:::zone-end

## ADO.NET persistence rationale

The principles for ADO.NET-backed persistence storage are:

1. Keep business-critical data safe and accessible while data, data format, and code evolve.
1. Take advantage of vendor-specific and storage-specific functionality.

In practice, this means adhering to ADO.NET implementation goals and including some added implementation logic in ADO.NET-specific storage providers that allow the shape of the data in storage to evolve.

In addition to the usual storage provider capabilities, the ADO.NET provider has built-in capability to:

1. Change storage data from one format to another (e.g., from JSON to binary) when round-tripping state.
1. Shape the type to be saved or read from storage in arbitrary ways. This allows the state version to evolve.
1. Stream data out of the database.

You can apply both `1.` and `2.` based on arbitrary decision parameters, such as *grain ID*, *grain type*, or *payload data*.

This allows you to choose a serialization format, for example, [Simple Binary Encoding (SBE)](https://github.com/real-logic/simple-binary-encoding), and implement <xref:Orleans.Storage.IStorageDeserializer> and <xref:Orleans.Storage.IStorageSerializer>. The built-in serializers were built using this method:

- <xref:Orleans.Storage.OrleansStorageDefaultXmlSerializer>
- <xref:Orleans.Storage.OrleansStorageDefaultXmlDeserializer>
- <xref:Orleans.Storage.OrleansStorageDefaultJsonSerializer>
- <xref:Orleans.Storage.OrleansStorageDefaultJsonDeserializer>
- <xref:Orleans.Storage.OrleansStorageDefaultBinarySerializer>
- <xref:Orleans.Storage.OrleansStorageDefaultBinaryDeserializer>

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

After implementing a custom serializer, configure it using the <xref:Orleans.Configuration.AdoNetGrainStorageOptions.GrainStorageSerializer> property as described in the [Configure serialization](#configure-serialization) section above. Your custom serializer must implement <xref:Orleans.Storage.IGrainStorageSerializer>.

:::zone-end
:::zone target="docs" pivot="orleans-3-x"

After implementing the serializers, they're registered internally with the <xref:Orleans.Storage.AdoNetGrainStorage.StorageSerializationPicker> property in <xref:Orleans.Storage.AdoNetGrainStorage>. `StorageSerializationPicker` is the default implementation of `IStorageSerializationPicker`. You can see an example of changing the data storage format or using serializers in [RelationalStoreTests](https://github.com/dotnet/orleans/blob/main/test/Extensions/Orleans.AdoNet.Tests/StorageTests/RelationalStoreTests.cs). Note that direct application-level access to `AdoNetGrainStorage` to configure serialization isn't available in Orleans 3.x.

:::zone-end

## Design goals

### 1. Allow use of any backend with an ADO.NET provider

This should cover the broadest possible set of backends available for .NET, which is a factor in on-premises installations. Some providers are listed at [ADO.NET overview](../../../framework/data/adonet/ado-net-overview.md), but not all are listed, such as [Teradata](https://downloads.teradata.com/download/connectivity/net-data-provider-for-teradata).

### 2. Maintain potential to tune queries and database structure, even while a deployment is running

In many cases, third parties host servers and databases under contract. It's not unusual to find virtualized hosting environments where performance fluctuates due to unforeseen factors like noisy neighbors or faulty hardware. Altering and redeploying Orleans binaries (due to contractual reasons) or even application binaries might not be possible. However, tweaking database deployment parameters is usually possible. Altering *standard components*, such as Orleans binaries, requires a lengthier optimization procedure for a given situation.

### 3. Allow use of vendor-specific and version-specific abilities

Vendors implement different extensions and features in their products. It's sensible to use these features when available. Examples include [native UPSERT](https://www.postgresql.org/about/news/1636/) or [PipelineDB](https://github.com/pipelinedb/pipelinedb/blob/master/README.md) in PostgreSQL, and [PolyBase](/sql/relational-databases/polybase/get-started-with-polybase) or [natively compiled tables and stored procedures](/sql/relational-databases/in-memory-oltp/native-compilation-of-tables-and-stored-procedures) in SQL Server.

### 4. Enable optimization of hardware resources

When designing an application, you can often anticipate which data needs faster insertion and which data is more suitable for cheaper *cold storage* (e.g., splitting data between SSD and HDD). Additional considerations include the physical location of data (some storage might be more expensive, like SSD RAID vs. HDD RAID, or more secure) or other decision factors. Related to *point 3*, some databases offer special partitioning schemes, such as SQL Server [Partitioned Tables and Indexes](/sql/relational-databases/partitions/partitioned-tables-and-indexes).

These principles apply throughout the application lifecycle. Considering that one of Orleans' core principles is high availability, it should be possible to adjust the storage system without interrupting the Orleans deployment. It should also be possible to adjust queries based on data and other application parameters. Brian Harry's [blog post](https://devblogs.microsoft.com/bharry/a-bit-more-on-the-feb-3-and-4-incidents/) provides an example of dynamic changes:

> When a table is small, it almost doesn't matter what the query plan is. When it's medium, an OK query plan is fine, but when it's huge (millions upon millions or billions of rows), even a slight variation in the query plan can kill you. For this reason, we hint our sensitive queries heavily.

### 5. Make no assumptions about tools, libraries, or deployment processes

Many organizations are familiar with specific database tools, such as [Dacpac](/sql/relational-databases/data-tier-applications/data-tier-applications) or [Redgate](https://www.red-gate.com/). Deploying a database might require permission or a specific person, like someone in a DBA role. Usually, this also means having the target database layout and a rough sketch of the queries the application produces to estimate the load. Processes, possibly influenced by industry standards, might mandate script-based deployment. Having queries and database structures in external scripts makes this possible.

### 6. Use the minimum interface functionality needed to load ADO.NET libraries and functionality

This approach is both fast and exposes less surface area to potential discrepancies in ADO.NET library implementations.

### 7. Make the design shardable

When appropriate (e.g., in a relational storage provider), make the design readily shardable. For instance, this means avoiding database-dependent data like `IDENTITY` columns. Information distinguishing row data should rely only on data from the actual parameters.

### 8. Make the design easy to test

Creating a new backend should ideally be as simple as translating an existing deployment script into the SQL dialect of the target backend, adding a new connection string to the tests (assuming default parameters), checking if the database is installed, and then running the tests against it.

### 9. Considering the previous points, make porting scripts for new backends and modifying existing backend scripts as transparent as possible

## Realization of the goals

The Orleans framework doesn't know about deployment-specific hardware (which might change during active deployment), data changes during the deployment lifecycle, or certain vendor-specific features usable only in specific situations. For this reason, the interface between the database and Orleans should adhere to the minimum set of abstractions and rules to meet these goals, ensure robustness against misuse, and facilitate testing. See [Cluster management](../../implementation/cluster-management.md) and the concrete [membership protocol implementation](https://github.com/dotnet/orleans/blob/main/src/Orleans.Core/SystemTargetInterfaces/IMembershipTable.cs). Also, the SQL Server implementation contains SQL Server edition-specific tuning. The interface contract between the database and Orleans is defined as follows:

1. The general idea is that data is read and written through Orleans-specific queries. Orleans operates on column names and types when reading, and on parameter names and types when writing.
1. Implementations **must** preserve input and output names and types. Orleans uses these parameters to read query results by name and type. Vendor-specific and deployment-specific tuning is allowed, and contributions are encouraged as long as the interface contract is maintained.
1. Implementations across vendor-specific scripts **should** preserve constraint names. This simplifies troubleshooting through uniform naming across concrete implementations.
1. **Version** – or **ETag** in application code – represents a unique version for Orleans. The type of its actual implementation isn't important as long as it represents a unique version. In the implementation, Orleans code expects a signed 32-bit integer.
1. To be explicit and remove ambiguity, Orleans expects some queries to return either **TRUE as > 0** value or **FALSE as = 0** value. The number of affected or returned rows doesn't matter. If an error is raised or an exception is thrown, the query **must** ensure the entire transaction rolls back and can either return FALSE or propagate the exception.
1. Currently, all but one query are single-row inserts or updates (note: you could replace `UPDATE` queries with `INSERT`, provided the associated `SELECT` queries performed the last write).

Database engines support in-database programming. This is similar to loading an executable script and invoking it to execute database operations. In pseudocode, it could be depicted as:

```csharp
const int Param1 = 1;
const DateTime Param2 = DateTime.UtcNow;
const string queryFromOrleansQueryTableWithSomeKey =
    "SELECT column1, column2 "+
    "FROM <some Orleans table> " +
    "WHERE column1 = @param1 " +
    "AND column2 = @param2;";
TExpected queryResult =
    SpecificQuery12InOrleans<TExpected>(query, Param1, Param2);
```

These principles are also [included in the database scripts](../../host/configuration-guide/adonet-configuration.md).

## Ideas for applying customized scripts

1. Alter scripts in `OrleansQuery` for grain persistence using `IF ELSE` so that some state saves using the default `INSERT`, while other grain states might use [memory-optimized tables](/sql/relational-databases/in-memory-oltp/memory-optimized-tables). Alter the `SELECT` queries accordingly.
1. Use the idea in `1.` to take advantage of other deployment- or vendor-specific aspects, such as splitting data between `SSD` and `HDD`, putting some data in encrypted tables, or perhaps inserting statistics data via SQL Server-to-Hadoop or even [linked servers](/sql/relational-databases/linked-servers/linked-servers-database-engine).

You can test the altered scripts by running the Orleans test suite or directly in the database using, for instance, a [SQL Server Unit Test Project](/previous-versions/sql/sql-server-data-tools/jj851212(v=vs.103)).

## Guidelines for adding new ADO.NET providers

1. Add a new database setup script according to the [Realization of the goals](#realization-of-the-goals) section above.
1. Add the vendor ADO invariant name to <xref:Orleans.SqlUtils.AdoNetInvariants> and ADO.NET provider-specific data to [DbConstantsStore](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Shared/Storage/DbConstantsStore.cs). These are potentially used in some query operations, for example,, to select the correct statistics insert mode (i.e., `UNION ALL` with or without `FROM DUAL`).
1. Orleans has comprehensive tests for all system stores: membership, reminders, and statistics. Add tests for the new database script by copy-pasting existing test classes and changing the ADO invariant name. Also, derive from [RelationalStorageForTesting](https://github.com/dotnet/orleans/blob/main/test/Extensions/Orleans.AdoNet.Tests/RelationalUtilities/RelationalStorageForTesting.cs) to define test functionality for the ADO invariant.
