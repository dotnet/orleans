---
layout: page
title: Grain Persistence
---


## Grain Persistence Goals

1. Allow different grain types to use different types of storage providers (e.g., one uses Azure table, and one uses an ADO.NET one) or the same type of storage provider but with different configurations (e.g., both use Azure table, but one uses storage account #1 and one uses storage account #2)
2. Allow configuration of a storage provider instance to be swapped (e.g., Dev-Test-Prod) with just config file changes, and no code changes required.
3. Provide a framework to allow additional storage providers to be written later, either by the Orleans team or others.
4. Provide a minimal set of production-grade storage providers
5. Storage providers have complete control over how they store grain state data in persistent backing store. Corollary: Orleans is not providing a comprehensive ORM storage solution, but allows custom storage providers to support specific ORM requirements as and when required.

## Grain Persistence API

Grain types can be declared in one of two ways:

* Extend `Grain` if they do not have any persistent state, or if they will handle all persistent state themselves, or
* Extend `Grain<T>` if they have some persistent state that they want the Orleans runtime to handle.
Stated another way, by extending `Grain<T>` a grain type is automatically opted-in to the Orleans system managed persistence framework.

For the remainder of this section, we will only be considering Option #2 / `Grain<T>` because Option #1 grains will continue to run as now without any behavior changes.

## Grain State Storage

Grain classes that inherit from `Grain<T>` (where `T` is an application-specific state data type that needs to be persisted) will have their state loaded automatically from a specified storage.

Grains will be marked with a `[StorageProvider]` attribute that specifies a named instance of a storage provider to use for reading / writing the state data for this grain.

``` csharp
[StorageProvider(ProviderName="store1")]
public class MyGrain<MyGrainState> ...
{
  ...
}
```

The Orleans framework provides a mechanism to specify & register different storage providers and configure them using `ISiloHostBuilder`,

``` csharp
var silo = new SiloHostBuilder()
    .AddMemoryGrainStorage("DevStore")
    .AddAzureTableGrainStorage("store1", options => options.ConnectionString = "DefaultEndpointsProtocol=https;AccountName=data1;AccountKey=SOMETHING1")
    .AddAzureBlobGrainStorage("store2", options => options.ConnectionString = "DefaultEndpointsProtocol=https;AccountName=data2;AccountKey=SOMETHING2")
    .Build();
```

## Configuring IGrainStorage Providers
Orleans natively supports a range of IGrainStorage implementations, which you can use for your application to store grain state.
In this section, we will go over how to configure `AzureTableGrainStorage`, `AzureBlobGrainStorage`, `DynamoDBGrainStorage`, `MemoryGrainStorage`, and `AdoNetGrainStorage` in a silo.
Configuration of other `IGrainStorage` providers is similar.

### AzureTableGrainStorage Provider

``` csharp
var silo = new SiloHostBuilder()
    .AddAzureTableGrainStorage("TableStore", options => options.ConnectionString = "UseDevelopmentStorage=true")
    ...
    .Build();
```

The following settings are available for configuring `AzureTableGrainStorage` providers, through `AzureTableGrainStorageOptions`:

``` csharp
    /// <summary>
    /// Configuration for AzureTableGrainStorage
    /// </summary>
public class AzureTableStorageOptions
{
        /// <summary>
        /// Azure table connection string
        /// </summary>
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        /// <summary>
        /// Table name where grain stage is stored
        /// </summary>
        public string TableName { get; set; } = DEFAULT_TABLE_NAME;
        public const string DEFAULT_TABLE_NAME = "OrleansGrainState";

        /// <summary>
        /// Indicates if grain data should be deleted or reset to defaults when a grain clears it's state.
        /// </summary>
        public bool DeleteStateOnClear { get; set; } = false;

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialzed prior to use.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

        #region json serialization
        public bool UseJson { get; set; }
        public bool UseFullAssemblyNames { get; set; }
        public bool IndentJson { get; set; }
        public TypeNameHandling? TypeNameHandling { get; set; }
        #endregion json serialization
}
```

> __Note:__ State size should not exceed 64KB, a limit imposed by Azure Table Storage.

### AzureBlobGrainStorage Provider

``` csharp
var silo = new SiloHostBuilder()
    .AddAzureBlobGrainStorage("BlobStore", options => options.ConnectionString = "UseDevelopmentStorage=true")
    ...
    .Build();
```

The following settings are available for configuring `AzureBlobGrainStorage` providers, through `AzureBlobStorageOptions`:

``` csharp
public class AzureBlobStorageOptions
{
        /// <summary>
        /// Azure connection string
        /// </summary>
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        /// <summary>
        /// Container name where grain stage is stored
        /// </summary>
        public string ContainerName { get; set; } = DEFAULT_CONTAINER_NAME;
        public const string DEFAULT_CONTAINER_NAME = "grainstate";

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialzed prior to use.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

        #region json serialization
        public bool UseJson { get; set; }
        public bool UseFullAssemblyNames { get; set; }
        public bool IndentJson { get; set; }
        public TypeNameHandling? TypeNameHandling { get; set; }
        #endregion json serialization
}
```

### DynamoDBGrainStorage Provider

``` csharp
var silo = new SiloHostBuilder()
    .AddDynamoDBGrainStorage("DDBStore", options =>
    {
        options.AccessKey = "MY_ACCESS_KEY";
        options.SecretKey = "MY_SECRET_KEY";
        options.Service = "us-wes-1";
    })
    ...
    .Build();
```

The following settings are available for configuring `DynamoDBGrainStorage` providers, through `DynamoDBStorageOptions`:

``` csharp
public class DynamoDBStorageOptions
{
        /// <summary>
        /// Gets or sets a unique identifier for this service, which should survive deployment and redeployment.
        /// </summary>
        public string ServiceId { get; set; } = string.Empty;

        /// <summary>
        /// AccessKey string for DynamoDB Storage
        /// </summary>
        [Redact]
        public string AccessKey { get; set; }

        /// <summary>
        /// Secret key for DynamoDB storage
        /// </summary>
        [Redact]
        public string SecretKey { get; set; }

        /// <summary>
        /// DynamoDB Service name 
        /// </summary>
        public string Service { get; set; }

        /// <summary>
        /// Read capacity unit for DynamoDB storage
        /// </summary>
        public int ReadCapacityUnits { get; set; } = DynamoDBStorage.DefaultReadCapacityUnits;

        /// <summary>
        /// Write capacity unit for DynamoDB storage
        /// </summary>
        public int WriteCapacityUnits { get; set; } = DynamoDBStorage.DefaultWriteCapacityUnits;

        /// <summary>
        /// DynamoDB table name.
        /// Defaults to 'OrleansGrainState'.
        /// </summary>
        public string TableName { get; set; } = "OrleansGrainState";

        /// <summary>
        /// Indicates if grain data should be deleted or reset to defaults when a grain clears it's state.
        /// </summary>
        public bool DeleteStateOnClear { get; set; } = false;

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialzed prior to use.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

        #region JSON Serialization
        public bool UseJson { get; set; }
        public bool UseFullAssemblyNames { get; set; }
        public bool IndentJson { get; set; }
        public TypeNameHandling? TypeNameHandling { get; set; }
        #endregion
    }
```

### ADO.NET Grain Storage Provider

The ADO .NET Grain Storage provider allows you to store grain state in relational databases.
Currently following databases are supported:

- SQL Server
- MySQL/MariaDB
- PostgreSQL
- Oracle

First, install the base package:

```
Install-Package Microsoft.Orleans.Persistence.AdoNet
```
After you restore on the nuget package for your project, you will
find different SQL scripts for the supported database vendors, which are copied to project directory \OrleansAdoNetContent where each of supported ADO.NET extensions has its own directory.You can also get them from the [Orleans.Persistence.AdoNet repository](https://github.com/dotnet/orleans/tree/master/src/AdoNet/Orleans.Persistence.AdoNet).
Create a database, and then run the appropriate script to create the tables.

The next steps are to install a second NuGet package (see table below) specific to the
database vendor you want, and to configure the storage provider either programmatically or
via XML configuration.

| Database        | Script                                                                                                                                       | NuGet Package                                                                  | AdoInvariant             | Remarks              |
|-----------------|----------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------|--------------------------|----------------------|
| SQL Server      | [SQLServer-Persistence.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/SQLServer-Persistence.sql)   | [System.Data.SqlClient](https://www.nuget.org/packages/System.Data.SqlClient/) | System.Data.SqlClient    |                      |
| MySQL / MariaDB | [MySQL-Persistence.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/MySQL-Persistence.sql)           | [MySql.Data](https://www.nuget.org/packages/MySql.Data/)                       | MySql.Data.MySqlClient   |                      |
| PostgreSQL      | [PostgreSQL-Persistence.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/PostgreSQL-Persistence.sql) | [Npgsql](https://www.nuget.org/packages/Npgsql/)                               | Npgsql                   |                      |
| Oracle          | [Oracle-Persistence.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/Oracle-Persistence.sql)         | [ODP.net](https://www.nuget.org/packages/Oracle.ManagedDataAccess/)            | Oracle.DataAccess.Client | No .net Core support |

The following is an example of how to configure an ADO.NET storage provider via `ISiloHostBuilder`:

```csharp
var siloHostBuilder = new SiloHostBuilder()
    .AddAdoNetGrainStorage("OrleansStorage", options=>
    {
        options.Invariant = "<Invariant>";
        options.ConnectionString = "<ConnectionString>";
        options.UseJsonFormat = true;
    });
```

Essentially, you only need to set the database-vendor-specific connection string and an
`Invariant` (see table above) that identifies the vendor. You may also choose the format in which the data
is saved, which may be either binary (default), JSON, or XML. While binary is the most compact
option, it is opaque and you will not be able to read or work with the data. JSON is the
recommended option.

You can set the following properties via `AdoNetGrainStorageOptions`:

```csharp

/// <summary>
/// Options for AdonetGrainStorage
/// </summary>
public class AdoNetGrainStorageOptions
{
        /// <summary>
        /// Connection string for AdoNet storage.
        /// </summary>
        [Redact]
        public string ConnectionString { get; set; }

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialzed prior to use.
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

        #region json serialization related settings
        /// <summary>
        /// Whether storage string payload should be formatted in JSON.
        /// <remarks>If neither <see cref="UseJsonFormat"/> nor <see cref="UseXmlFormat"/> is set to true, then BinaryFormatSerializer will be configured to format storage string payload.</remarks>
        /// </summary>
        public bool UseJsonFormat { get; set; }
        public bool UseFullAssemblyNames { get; set; }
        public bool IndentJson { get; set; }
        public TypeNameHandling? TypeNameHandling { get; set; }
        #endregion
        /// <summary>
        /// Whether storage string payload should be formatted in Xml.
        /// <remarks>If neither <see cref="UseJsonFormat"/> nor <see cref="UseXmlFormat"/> is set to true, then BinaryFormatSerializer will be configured to format storage string payload.</remarks>
        /// </summary>
        public bool UseXmlFormat { get; set; }
}
```

The ADO.NET persistence has functionality to version data and define arbitrary (de)serializers with arbitrary application rules and streaming, but currently there is no method to expose them to application code.
More information in [ADO.NET Persistence Rationale](#ADONETPersistenceRationale).

### MemoryGrainStorage

`MemoryGrainStorage` is a simple grain storage implementation which does not really use a persistent data store underneath.
It is convenient to learn to work with Grain Storages quickly, but is not intended to be used in production scenarios.

> __Note:__ This provider persists state to volatile memory which is erased at silo shut down. Use only for testing.

Here's how to set up a memory storage provider via `ISiloHostBuilder`

```csharp
var siloHostBuilder = new SiloHostBuilder()
    .AddMemoryGrainStorage("OrleansStorage", options=>options.NumStorageGrains = 10);
```

You can set the following configuration properties via `MemoryGrainStorageOptions`

```csharp
/// <summary>
/// Options for MemoryGrainStorage
/// </summary>
public class MemoryGrainStorageOptions
{
        /// <summary>
        /// Default number of queue storage grains.
        /// </summary>
        public const int NumStorageGrainsDefaultValue = 10;
        /// <summary>
        /// Number of store grains to use.
        /// </summary>
        public int NumStorageGrains { get; set; } = NumStorageGrainsDefaultValue;

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialzed prior to use.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        /// <summary>
        /// Default init stage
        /// </summary>
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;
}
```

## Notes on Storage Providers

If there is no `[StorageProvider]` attribute specified for a `Grain<T>` grain class, then a provider named `Default` will be searched for instead.
If not found then this is treated as a missing storage provider.

If a prorage provider referenced by a grain class is not added to silo at configuration time, grains of that type will fail to activate at run time, and calls to them will be failing an `Orleans.Storage.BadProviderConfigException` error specifying that the grain type is not loaded.
But the rest of the grain types will not be affected.

Different grain types can use different configured storage providers, even if both are the same type: for example, two different Azure table storage provider instances, connected to different Azure storage accounts (see config file example above).

All configuration details for storage providers is defined through ISiloHostBuilder.
There are _no_ mechanisms provided at this time to dynamically update or change the list of storage providers used by a silo.
However, this is a prioritization / workload constraint rather than a fundamental design constraint.

## State Persitence API

There are two parts to the state persistence APIs: grain state API and storage provider API.

## Grain State API

The grain state storage functionality in the Orleans Runtime will provide read and write operations to automatically populate / save the `GrainState` data object for that grain.
Under the covers, these functions will be connected (within the code generated by Orleans client-gen tool) through to the appropriate persistence provider configured for that grain.

## Grain State Read / Write Functions

Grain state will automatically be read when the grain is activated, but grains are responsible for explicitly triggering the write for any changed grain state as and when necessary.
See the [Failure Modes](#FailureModes) section below for details of error handling mechanisms.

`GrainState` will be read automatically (using the equivalent of `base.ReadStateAsync()`) _before_ the `OnActivateAsync()` method is called for that activation.
`GrainState` will not be refreshed before any method calls to that grain, unless the grain was activated for this call.

During any grain method call, a grain can request the Orleans runtime to write the current grain state data for that activation to the designated storage provider by calling `base.WriteStateAsync()`.
The grain is responsible for explicitly performing write operations when they make significant updates to their state data.
Most commonly, the grain method will return the `base.WriteStateAsync()` `Task` as the final result `Task` returned from that grain method, but it is not required to follow this pattern.
The runtime will not automatically update stored grain state after any grain methods.

During any grain method or timer callback handler in the grain, the grain can request the Orleans runtime to re-read the current grain state data for that activation from the designated storage provider by calling `base.ReadStateAsync()`.
This will completely overwrite any current state data currently stored in the grain state object with the latest values read from persistent store.

An opaque provider-specific `Etag` value (`string`) _may_ be set by a storage provider as part of the grain state metadata populated when state was read.
Some providers may choose to leave this as `null` if they do not use `Etag`s.

Conceptually, the Orleans Runtime will take a deep copy of the grain state data object for its own use during any write operations. Under the covers, the runtime _may_ use optimization rules and heuristics to avoid performing some or all of the deep copy in some circumstances, provided that the expected logical isolation semantics are preserved.

## Sample Code for Grain State Read / Write Operations

Grains must extend the `Grain<T>` class in order to participate in the Orleans grain state persistence mechanisms.
The `T` in the above definition will be replaced by an application-specific grain state class for this grain; see the example below.

The grain class should also be annotated with a `[StorageProvider]` attribute that tells the runtime which storage provider (instance) to use with grains of this type.

``` csharp
public class MyGrainState
{
  public int Field1 { get; set; }
  public string Field2 { get; set; }
}

[StorageProvider(ProviderName="store1")]
public class MyPersistenceGrain : Grain<MyGrainState>, IMyPersistenceGrain
{
  ...
}
```

## Grain State Read

The initial read of the grain state will occur automatically by the Orleans runtime before the grain’s `OnActivateAsync()` method is called; no application code is required to make this happen.
From that point forward, the grain’s state will be available through the `Grain<T>.State` property inside the grain class.

## Grain State Write

After making any appropriate changes to the grain’s in-memory state, the grain should call the `base.WriteStateAsync()` method to write the changes to the persistent store via the defined storage provider for this grain type.
This method is asynchronous and returns a `Task` that will typically be returned by the grain method as its own completion Task.


``` csharp
public Task DoWrite(int val)
{
  State.Field1 = val;
  return base.WriteStateAsync();
}
```

## Grain State Refresh

If a grain wishes to explicitly re-read the latest state for this grain from backing store, the grain should call the `base.ReadStateAsync()` method.
This will reload the grain state from persistent store, via the defined storage provider for this grain type, and any previous in-memory copy of the grain state will be overwritten and replaced when the `ReadStateAsync()` `Task` completes.

``` csharp
public async Task<int> DoRead()
{
  await base.ReadStateAsync();
  return State.Field1;
}
```

## Failure Modes for Grain State Persistence Operations <a name="FailureModes"></a>

### Failure Modes for Grain State Read Operations

Failures returned by the storage provider during the initial read of state data for that particular grain will result in the activate operation for that grain to be failed; in this case, there will _not_ be any call to that grain’s `OnActivateAsync()` life cycle callback method.
The original request to that grain which caused the activation will be faulted back to the caller the same way as any other failure during grain activation.
Failures encountered by the storage provider to read state data for a particular grain will result in the `ReadStateAsync()` `Task` to be faulted.
The grain can choose to handle or ignore that faulted `Task`, just like any other `Task` in Orleans.

Any attempt to send a message to a grain which failed to load at silo startup time due to a missing / bad storage provider config will return the permanent error `Orleans.BadProviderConfigException`.

### Failure Modes for Grain State Write Operations

Failures encountered by the storage provider to write state data for a particular grain will result in the `WriteStateAsync()` `Task` to be faulted.
Usually, this will mean the grain call will be faulted back to the client caller provided the `WriteStateAsync()` `Task` is correctly chained in to the final return `Task` for this grain method.
However, it will be possible for certain advanced scenarios to write grain code to specifically handle such write errors, just like they can handle any other faulted `Task`.

Grains that execute error-handling / recovery code _must_ catch exceptions / faulted `WriteStateAsync()` `Task`s and not re-throw to signify that they have successfully handled the write error.

## Storage Provider API

There is a service provider API for writing additional persistence providers – `IGrainStorage`.

The Persistence Provider API covers read and write operations for GrainState data.

``` csharp
/// <summary>
/// Interface to be implemented for a storage able to read and write Orleans grain state data.
/// </summary>
public interface IGrainStorage
{
        /// <summary>Read data function for this storage instance.</summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainReference">Grain reference object for this grain.</param>
        /// <param name="grainState">State data object to be populated for this grain.</param>
        /// <returns>Completion promise for the Read operation on the specified grain.</returns>
        Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState);

        /// <summary>Write data function for this storage instance.</summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainReference">Grain reference object for this grain.</param>
        /// <param name="grainState">State data object to be written for this grain.</param>
        /// <returns>Completion promise for the Write operation on the specified grain.</returns>
        Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState);

        /// <summary>Delete / Clear data function for this storage instance.</summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainReference">Grain reference object for this grain.</param>
        /// <param name="grainState">Copy of last-known state data object for this grain.</param>
        /// <returns>Completion promise for the Delete operation on the specified grain.</returns>
        Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState);
}
```

## Storage Provider Semantics

Any attempt to perform a write operation when the storage provider detects an `Etag` constraint violation _should_ cause the write `Task` to be faulted with transient error `Orleans.InconsistentStateException` and wrapping the underlying storage exception.

``` csharp
public class InconsistentStateException : AggregateException
{
  /// <summary>The Etag value currently held in persistent storage.</summary>
  public string StoredEtag { get; private set; }
  /// <summary>The Etag value currently held in memory, and attempting to be updated.</summary>
  public string CurrentEtag { get; private set; }

  public InconsistentStateException(
    string errorMsg,
    string storedEtag,
    string currentEtag,
    Exception storageException
    ) : base(errorMsg, storageException)
  {
    this.StoredEtag = storedEtag;
    this.CurrentEtag = currentEtag;
  }

  public InconsistentStateException(string storedEtag, string currentEtag, Exception storageException)
    : this(storageException.Message, storedEtag, currentEtag, storageException)
  { }
}
```


Any other failure conditions from a write operation _should_ cause the write `Task` to be broken with an exception containing the underlying storage exception.

## Data Mapping

Individual storage providers should decide how best to store grain state – blob (various formats / serialized forms) or column-per-field are obvious choices.

The basic storage provider for Azure Table encodes state data fields into a single table column using Orleans binary serialization.


## ADO.NET Persistence Rationale <a name="ADONETPersistenceRationale"></a>

The principles for ADO.NET backed persistence storage are:

1. Keep business critical data safe an accessible while data, the format of data and code evolve.
2. Take advantenge of vendor and storage specific functionality.

In practice this means adhering to [ADO.NET implementation goals](../Runtime-Implementation-Details/Relational-Storage.md)
and some added implementation logic in ADO.NET specific storage provider that allow evolving the shape of the data in the storage.

In addition to the usual storage provider capabilities, the ADO.NET provider has built-in capability to

1. Change storage data format from one format to another format (e.g. from JSON to binary) when roundtripping state.
2. Shape the type to be saved or read from the storage in arbitrary ways. This helps to evolve the version state.
3. Stream data out of the database.

Both `1.` and `2.` can be applied on arbitrary decision parameters, such as *grain ID*, *grain type*, *payload data*.

This happen so that one chooses a format, e.g. [Simple Binary Encoding (SBE)](https://github.com/real-logic/simple-binary-encoding) and implements
[IStorageDeserializer](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/Storage/Provider/IStorageDeserializer.cs) and [IStorageSerializer](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/Storage/Provider/IStorageSerializer.cs).
The built-in (de)serializers have been built using this method. The [OrleansStorageDefault<format>(De)Serializer](https://github.com/dotnet/orleans/tree/master/src/AdoNet/Orleans.Persistence.AdoNet/Storage/Provider) can be used as examples
on how to implement other formats.

When the (de)serializers have been implemented, they need to ba added to the `StorageSerializationPicker` property in [AdoNetGrainStorage](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/Storage/Provider/AdoNetGrainStorage.cs).
This is an implementation of [IStorageSerializationPicker](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/Storage/Provider/IStorageSerializationPicker.cs). By default
[StorageSerializationPicker](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/Storage/Provider/StorageSerializationPicker.cs) will be used. And example of changing data storage format
or using (de)serializers can be seen at [RelationalStorageTests](https://github.com/dotnet/orleans/blob/master/test/TesterAdoNet/StorageTests/Relational/RelationalStorageTests.cs).

Currently there is no method to expose this to Orleans application consumption as there is no method to access the framework created [AdoNetGrainStorage](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/Storage/Provider/AdoNetGrainStorage.cs).
