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

## Grain State Stores

Grain classes that inherit from `Grain<T>` (where `T` is an application-specific state data type derived from `GrainState`) will have their state loaded automatically from a specified storage.

Grains will be marked with a `[StorageProvider]` attribute that specifies a named instance of a storage provider to use for reading / writing the state data for this grain.

``` csharp
[StorageProvider(ProviderName="store1")]
public class MyGrain<MyGrainState> ...
{
  ...
}
```

The Orleans Provider Manager framework provides a mechanism to specify & register different storage providers and storage options in the silo config file.

```xml
<OrleansConfiguration xmlns="urn:orleans">
    <Globals>
    <StorageProviders>
        <Provider Type="Orleans.Storage.MemoryStorage" Name="DevStore" />
        <Provider Type="Orleans.Storage.AzureTableStorage" Name="store1"
            DataConnectionString="DefaultEndpointsProtocol=https;AccountName=data1;AccountKey=SOMETHING1" />
        <Provider Type="Orleans.Storage.AzureBlobStorage" Name="store2"
            DataConnectionString="DefaultEndpointsProtocol=https;AccountName=data2;AccountKey=SOMETHING2"  />
    </StorageProviders>
```

## Configuring Storage Providers

### AzureTableStorage

```xml
<Provider Type="Orleans.Storage.AzureTableStorage" Name="TableStore"
    DataConnectionString="UseDevelopmentStorage=true" />
```

The following attributes can be added to the `<Provider />` element to configure the provider:

* __`DataConnectionString="..."`__ (mandatory) - The Azure storage connection string to use
* __`TableName="OrleansGrainState"`__ (optional) - The table name to use in table storage, defaults to `OrleansGrainState`
* __`DeleteStateOnClear="false"`__ (optional) - If true, the record will be deleted when grain state is cleared, otherwise an null record will be written, defaults to `false`
* __`UseJsonFormat="false"`__ (optional) - If true, the json serializer will be used, otherwise the Orleans binary serializer will be used, defaults to `false`
* __`UseFullAssemblyNames="false"`__ (optional) - (if `UseJsonFormat="true"`) Serializes types with full assembly names (true) or simple (false), defaults to `false`
* __`IndentJSON="false"`__ (optional) - (if `UseJsonFormat="true"`) Indents the serialized json, defaults to `false`

> __Note:__ state should not exceed 64KB, a limit imposed by Table Storage.

### AzureBlobStorage

```xml
<Provider Type="Orleans.Storage.AzureTableStorage" Name="BlobStore"
    DataConnectionString="UseDevelopmentStorage=true" />
```

The following attributes can be added to the `<Provider />` element to configure the provider:

* __`DataConnectionString="..."`__ (mandatory) - The Azure storage connection string to use
* __`ContainerName="grainstate"`__ (optional) - The blob storage container to use, defaults to `grainstate`
* __`UseFullAssemblyNames="false"`__ (optional) - Serializes types with full assembly names (true) or simple (false), defaults to `false`
* __`IndentJSON="false"`__ (optional) - Indents the serialized json, defaults to `false`

### DynamoDBStorageProvider

```xml
<Provider Type="Orleans.Storage.DynamoDBStorageProvider" Name="DDBStore"
    DataConnectionString="Service=us-wes-1;AccessKey=MY_ACCESS_KEY;SecretKey=MY_SECRET_KEY;" />
```

* __`DataConnectionString="..."`__ (mandatory) - The DynamoDB storage connection string to use. You can set `Service`,`AccessKey`, `SecretKey`, `ReadCapacityUnits` and `WriteCapacityUnits` in it.
* __`TableName="OrleansGrainState"`__ (optional) - The table name to use in table storage, defaults to `OrleansGrainState`
* __`DeleteStateOnClear="false"`__ (optional) - If true, the record will be deleted when grain state is cleared, otherwise an null record will be written, defaults to `false`
* __`UseJsonFormat="false"`__ (optional) - If true, the json serializer will be used, otherwise the Orleans binary serializer will be used, defaults to `false`
* __`UseFullAssemblyNames="false"`__ (optional) - (if `UseJsonFormat="true"`) Serializes types with full assembly names (true) or simple (false), defaults to `false`
* __`IndentJSON="false"`__ (optional) - (if `UseJsonFormat="true"`) Indents the serialized json, defaults to `false`


### ADO.NET Storage Provider (SQL Storage Provider)

Note that to use the it is necessary to deploy the database script to the database. It can be found in the The scripts are located in the Nuget library, similar to `\packages\Microsoft.Orleans.OrleansSqlUtils.n.n.n\lib\net<version>\SQLServer\` depending on version and database vendor.

```xml
<Provider Type="Orleans.SqlUtils.StorageProvider.SqlStorageProvider" Name="SqlStore" DataConnectionString="Data Source = (localdb)\MSSQLLocalDB; Database = OrleansTestStorage; Integrated Security = True; Asynchronous Processing = True; Max Pool Size = 200;" />
```
 
* __`DataConnectionString="..."`__ (mandatory) - The SQL connection string to use.
* __`UseJsonFormat="false"`__ (optional) - If true, the json serializer will be used, otherwise the Orleans binary serializer will be used, defaults to `false`.
* __`UseXmlFormat="false"`__ (optional) - If true, the .NET XML serializer will be used, otherwise the Orleans binary serializer will be used, defaults to `false`.
* __`UseBinaryFormat="false"`__ (the default) - If true, the Orleans binary data format will be used.

Note that pool size of 200 is quite a low figure.

The following is an example of programmatic configuration.

``` csharp
//props["RootDirectory"] = @".\Samples.FileStorage";
//config.Globals.RegisterStorageProvider<Samples.StorageProviders.OrleansFileStorage>("TestStore", props);
props[Orleans.Storage.AdoNetStorageProvider.DataConnectionStringPropertyName] = @"Data Source = (localdb)\MSSQLLocalDB; Database = OrleansTestStorage; Integrated Security = True; Asynchronous Processing = True; Max Pool Size = 200;";
props[Orleans.Storage.AdoNetStorageProvider.UseJsonFormatPropertyName] = "true"; //Binary, the default option, is more efficient. This is for illustrative purposes.
config.Globals.RegisterStorageProvider<Orleans.Storage.AdoNetStorageProvider>("TestStore", props);
```

A quick way to test this is to (see in the aforementioned the few commented lines)

1. Open `\Samples\StorageProviders`.
2. On the package manager console, run: `Install-Package Microsoft.Orleans.OrleansSqlUtils -project Test.Client`.
3. Update all the Orleans packages in the solution, run: `Get-Package | where Id -like 'Microsoft.Orleans.*' | foreach { update-package $_.Id }` (this is a precaution to make sure the packages are on same version).
4. Go to `OrleansHostWrapper.cs` and to the following

The ADO.NET persistence has functionality to version data and define arbitrary (de)serializers with arbitrary application rules and streaming, but currently
there is no method to expose them to application code. More information in [ADO.NET Persistence Rationale](#ADONETPersistenceRationale).


### MemoryStorage

```xml
<Provider Type="Orleans.Storage.MemoryStorage" Name="MemoryStorage"  />
```
> __Note:__ This provider persists state to volatile memory which is erased at silo shut down. Use only for testing.

* __`NumStorageGrains="10"`__ (optional) - The number of grains to use to store the state, defaults to `10`


### ShardedStorageProvider

```xml
<Provider Type="Orleans.Storage.ShardedStorageProvider" Name="ShardedStorage">
    <Provider />
    <Provider />
    <Provider />
</Provider>
```
Simple storage provider for writing grain state data shared across a number of other storage providers.

A consistent hash function (default is Jenkins Hash) is used to decide which
shard (in the order they are defined in the config file) is responsible for storing
state data for a specified grain, then the Read / Write / Clear request
is bridged over to the appropriate underlying provider for execution.

## Notes on Storage Providers

If there is no `[StorageProvider]` attribute specified for a `Grain<T>` grain class, then a provider named `Default` will be searched for instead.
If not found then this is treated as a missing storage provider.

If there is only one provider in the silo config file, it will be considered to be the `Default` provider for this silo.

A grain that uses a storage provider which is not present and defined in the silo configuration when the silo loads will fail to load, but the rest of the grains in that silo can still load and run.
Any later calls to that grain type will fail with an `Orleans.Storage.BadProviderConfigException` error specifying that the grain type is not loaded.

The storage provider instance to use for a given grain type is determined by the combination of the storage provider name defined in the `[StorageProvider]` attribute on that grain type, plus the provider type and configuration options for that provider defined in the silo config.

Different grain types can use different configured storage providers, even if both are the same type: for example, two different Azure table storage provider instances, connected to different Azure storage accounts (see config file example above).

All configuration details for storage providers is defined statically in the silo configuration that is read at silo startup.
There are _no_ mechanisms provided at this time to dynamically update or change the list of storage providers used by a silo.
However, this is a prioritization / workload constraint rather than a fundamental design constraint.

## State Storage APIs

There are two main parts to the grain state / persistence APIs: Grain-to-Runtime and Runtime-to-Storage-Provider.

## Grain State Storage API

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
public interface MyGrainState : GrainState
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

## Storage Provider Framework

There is a service provider API for writing additional persistence providers – `IStorageProvider`.

The Persistence Provider API covers read and write operations for GrainState data.

``` csharp
public interface IStorageProvider
{
  Logger Log { get; }
  Task Init();
  Task Close();

  Task ReadStateAsync(string grainType, GrainId grainId, GrainState grainState);
  Task WriteStateAsync(string grainType, GrainId grainId, GrainState grainState);
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

In practice this means adhering to [ADO.NET implementation goals](../Runtime-Implementation-Details/Relational-Storage)
and some added implementation logic in ADO.NET specific storage provider that allow evolving the shape of the data in the storage.

In addition to the usual storage provider capabilities, the ADO.NET provider has built-in capability to

1. Change storage data format from one format to another format (e.g. from JSON to binary) when roundtripping state.
2. Shape the type to be saved or read from the storage in arbitrary ways. This helps to evolve the version state.
3. Stream data out of the database.

Both `1.` and `2.` can be applied on arbitrary decision parameters, such as *grain ID*, *grain type*, *payload data*.

This happen so that one chooses a format, e.g. [Simple Binary Encoding (SBE)](https://github.com/real-logic/simple-binary-encoding) and implements
(IStorageDeserializer)[https://github.com/dotnet/orleans/blob/master/src/OrleansSQLUtils/Storage/Provider/IStorageDeserializer.cs] and [IStorageSerializer](https://github.com/dotnet/orleans/blob/master/src/OrleansSQLUtils/Storage/Provider/IStorageSerializer.cs).
The built-in (de)serializers have been built using this method. The [OrleansStorageDefault<format>(De)Serializer](https://github.com/dotnet/orleans/tree/master/src/OrleansSQLUtils/Storage/Provider) can be used as examples
on how to implement other formats.

When the (de)serializers have been implemented, they need to ba added to the `StorageSerializationPicker` property in [AdoNetStorageProvider](https://github.com/dotnet/orleans/blob/master/src/OrleansSQLUtils/Storage/Provider/AdoNetStorageProvider.cs).
This is an implementation of [IStorageSerializationPicker](https://github.com/dotnet/orleans/blob/master/src/OrleansSQLUtils/Storage/Provider/IStorageSerializationPicker.cs). By default
[StorageSerializationPicker](https://github.com/dotnet/orleans/blob/master/src/OrleansSQLUtils/Storage/Provider/StorageSerializationPicker.cs) will be used. And example of changing data storage format
or using (de)serializers can be seen at [RelationalStorageTests]https://github.com/dotnet/orleans/blob/master/test/TesterInternal/StorageTests/Relational/RelationalStorageTests.cs).

Currently there is no method to expose this to Orleans application consumption as there is no method to access the framework created [AdoNetStorageProvider](https://github.com/dotnet/orleans/blob/master/src/OrleansSQLUtils/Storage/Provider/AdoNetStorageProvider.cs) instance.
