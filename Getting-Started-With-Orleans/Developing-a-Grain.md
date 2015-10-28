---
layout: page
title: Developing a Grain
---
{% include JB/setup %}

In this section we walk through the steps involved in defining and using a new `Player` grain type as used in the `Presence` sample application in the Orleans SDK. 
The grain type we define will have one property that returns a reference to the game the player is currently in, and two methods for joining and leaving a game.

We will create three separate pieces of code: the grain interface definition, the grain implementation, and a standard C# class that uses the grain. 
Each of these belongs in a different project, built into a different DLL: the interface needs to be available on both the "client" and "server" sides, while the implementation class should be hidden from the client, and the client class from the server.

The interface project should be created using the Visual Studio "Orleans Grain Interface Collection" template that is included in the Orleans SDK, and the grain implementation project should be created using the Visual Studio "Orleans Grain Class Collection" template. 
The grain client project can use any standard .NET code project template, such as the standard Console Application or Class Library templates.

A grain cannot be explicitly created or deleted. 
It always exists "virtually" and is activated automatically when a request is sent to it. 
A grain has either a GUID, string or a long integer key within the grain type. 
Application code creates a reference to a grain by calling the `GetGrain<TGrainType>(Guid id)` or `GetGrain<TGrainType>(long id)` or other overlaods of a generic grain factory methods for a specific grain identity. 
The `GetGrain()` call is a purely local operation to create a grain reference. 
It does not trigger creation of a grain activation and has not impact on its life cycle. 
A grain activation is automatically created by the Orleans runtime upon a first request sent to the grain.

A grain interface must inherit from one of the `IGrainWithXKey`.interfaces where X is the type of the key used. 
The GUID, string or long integer key of a grain can later be retrieved via the `GetPrimaryKey()` or `GetPrimaryKeyLong()` extension methods, respectively.

## Defining the Grain Interface

A grain type is defined by an interface that inherits from one of the `IGrainWithXKey` marker interfaces like 'IGrainWithGuidKey' or 'IGrainWithStringKey'.

All of the methods in the grain interface must return a `Task` or a `Task<T>` for .NET 4.5. 
The underlying type `T` for value `Task` must be serializable.

 Example:

``` csharp
public interface IPlayerGrain : IGrainWithGuidKey 
{ 
  Task<IGameGrain> GetCurrentGameAsync();
  Task JoinGameAsync(IGameGrain game); 
  Task LeaveGameAsync(IGameGrain game); 
} 
```

## Using the Grain Factory

After the grain interface has been defined, building the project originally created with the Orleans Visual Studio project template will use the Orleans-specific MSBuild targets to generate a client proxy classes corresponding to the user-defined grain interfaces and to merge this additional code back into the interface DLL. 
The code generation tool, _ClientGenerator.exe_, can also be invoked directly as a part of post-build processing. 
However this should be used with caution and is generally not recommended.

Application should use the generic grain factory class to get references to grains. Inside the grain code the factory is availiable via the protected GrainFactory class member property. On the client side the factory is availiable via the `GrainClient.GrainFactory` static field.

When running inside a grain the following code should be used to get the grain reference:

``` csharp
    this.GrainFactory.GetGrain<IPlayerGrain>(grainKey);
```
When running on the Orleans client side the following code should be used to get the grain reference:

``` csharp
    GrainClient.GrainFactory.GetGrain<IPlayerGrain>(grainKey);
```

## The Implementation Class

A grain type is materialized by a class that implements the grain type’s interface and inherits directly or indirectly from `Orleans.Grain`. 

The `PlayerGrain` grain class implements the `IPlayerGrain` interface. 

``` csharp
public class PlayerGrain : Grain, IPlayerGrain 
{ 
    private IGameGrain currentGame; 

    // Game the player is currently in. May be null. 
    public Task<IGameGrain> GetCurrentGameAsync()
    { 
       return Task.FromResult(currentGame);
    } 

    // Game grain calls this method to notify that the player has joined the game. 
    public Task JoinGameAsync(IGameGrain game) 
    {
       currentGame = game; 
       Console.WriteLine("Player {0} joined game {1}", this.GetPrimaryKey(), game.GetPrimaryKey()); 
       return TaskDone.Done; 
    } 

   // Game grain calls this method to notify that the player has left the game. 
   public Task LeaveGameAsync(IGameGrain game) 
   { 
       currentGame = null; 
       Console.WriteLine("Player {0} left game {1}", this.GetPrimaryKey(), game.GetPrimaryKey()); 
       return TaskDone.Done; 
   } 
} 
```

## Persistence of Grain State

This section provides details on the Orleans runtime mechanisms available to support "Grain Persistence".

Goals:

1. Allow different grain types to use different types of storage providers (e.g., one uses Azure table, and one uses SQL Azure) or the same type of storage provider but with different configurations (e.g., both use Azure table, but one uses storage account #1 and one uses storage account #2) 
2. Allow configuration of a storage provider instance to be swapped (e.g., Dev-Test-Prod) with just config file changes, and no code changes required. 
3. Provide a framework to allow additional storage providers to be written later, either by the Orleans team or others. 
4. Provide a minimal set of production-grade storage providers, both to demonstrate viability of the storage provider framework, and cover common storage types that will be used by a majority of users. Phase 1 will ship with a non-persistent in-memory store for developer testing scenarios, and a persistent unsharded Azure table storage provider. 
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

    <OrleansConfiguration xmlns="urn:orleans">
      <Globals>
        <StorageProviders>
           <Provider Type="Orleans.Storage.MemoryStorage" Name="DevStore" />
           <Provider Type="Orleans.Storage.AzureTableStorage" Name="store1"    
              DataConnectionString="DefaultEndpointsProtocol=https;AccountName=data1;AccountKey=SOMETHING1" />
           <Provider Type="Orleans.Storage.AzureTableStorage" Name="store2" 
             DataConnectionString="DefaultEndpointsProtocol=https;AccountName=data2;AccountKey=SOMETHING2"  />
        </StorageProviders>

Note: storage provider types `Orleans.Storage.AzureTableStorage` and `Orleans.Storage.MemoryStorage` are two standard storage providers built in to the Orleans runtime.

If there is no `[StorageProvider]` attribute specified for a `Grain<T>` grain class, then a provider named `Default` will be searched for instead. 
If not found then this is treated as a missing storage provider.

If there is only one provider in the silo config file, it will be considered to be the `Default` provider for this silo.

A grain that uses a storage provider which is not present and defined in the silo configuration when the silo loads will fail to load, but the rest of the grains in that silo can still load and run. 
Any later calls to that grain type will fail with an `Orleans.Storage.BadProviderConfigException` error specifying that the grain type is not loaded.

The storage provider instance to use for a given grain type is determined by the combination of the storage provider name defined in the `[StorageProvider]` attribute on that grain type, plus the provider type and configuration options for that provider defined in the silo config.

Different grain types can use different configured storage providers, even if both are the same type: for example, two different Azure table storage provider instances, connected to different Azure storage accounts (see config file example above).

For the Phase 1 implementation, all configuration details for storage providers will be defined statically in the silo configuration that is read at silo startup. 
There will be _no_ mechanisms provided at this time to dynamically update or change the list of storage providers used by a silo. 
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

##Next

[Developing a Client](Developing-a-Client)


