---
title: Custom grain storage sample project
description: Explore a custom grain storage sample project written with .NET Orleans.
ms.date: 08/14/2026
ms.topic: tutorial
ai-usage: ai-assisted
---

# Custom grain storage

In the tutorial on declarative actor storage, you learned how to allow grains to store their state in an Azure table using one of the built-in storage providers. While Azure is a great place to store your data, many alternatives exist. There are so many that supporting them all isn't feasible. Instead, Orleans is designed to let you easily add support for your preferred storage by writing a custom grain storage provider.

In this tutorial, you'll walk through how to write a simple file-based grain storage provider. A file system isn't the best place to store grain states because it's local, can have issues with file locks, and the last update date isn't sufficient to prevent inconsistency. However, it's an easy example to illustrate the implementation of a _grain storage_ provider.

## Get started

An Orleans grain storage provider is a class that implements <xref:Orleans.Storage.IGrainStorage>, included in the [Microsoft.Orleans.Core](https://www.nuget.org/packages/Microsoft.Orleans.Core) NuGet package. This sample also implements <xref:Orleans.ILifecycleParticipant`1>, where the lifecycle type is <xref:Orleans.Runtime.ISiloLifecycle>, so that it can initialize during the silo lifecycle. Start by creating a class named `FileGrainStorage`; the complete, compiling implementation appears at the end of the tutorial.

Each method implements the corresponding method in the <xref:Orleans.Storage.IGrainStorage> interface, accepting a generic type parameter for the underlying state type. The methods are:

- <xref:Orleans.Storage.IGrainStorage.ReadStateAsync*?displayProperty=nameWithType>: Reads the state of a grain.
- <xref:Orleans.Storage.IGrainStorage.WriteStateAsync*?displayProperty=nameWithType>: Writes the state of a grain.
- <xref:Orleans.Storage.IGrainStorage.ClearStateAsync*?displayProperty=nameWithType>: Clears the state of a grain.

All three methods receive the same arguments:

| Argument | Meaning |
| --- | --- |
| `stateName` | The logical name of this state record. For <xref:Orleans.Runtime.IPersistentState`1>, this is the state name configured by <xref:Orleans.Runtime.PersistentStateAttribute>; legacy <xref:Orleans.Grain`1> state uses `state`. A grain can have multiple named state records, so include this value in the storage key. Older versions of the interface called this argument `grainType`, but it doesn't describe the state object's .NET type. |
| `grainId` | The complete Orleans grain identity, including its grain type and primary key. Combine it with `stateName` to identify a record. Don't key records using only the primary key, because different grain types can use the same key. |
| `grainState` | The state container Orleans passes to the provider. Its <xref:Orleans.IGrainState`1.State> property contains the application state, <xref:Orleans.IGrainState`1.ETag> carries the provider's optimistic-concurrency token, and <xref:Orleans.IGrainState`1.RecordExists> indicates whether the record exists. A read populates these properties; a write or clear updates them to reflect the completed operation. |
| `T` | The declared .NET type of the state payload. Use `T` (or `typeof(T)`) for serialization and type metadata. It isn't a record identifier and can be shared by many grains and named state records. |

Therefore, `stateName` and `grainState.State?.GetType().Name` aren't interchangeable. The first identifies one of a grain's logical state records and is stable independently of the payload implementation. The second is only the runtime type name of the current payload; it can be `null`, can differ from `typeof(T)` when polymorphism is involved, and can change during a refactoring. The sample uses `stateName` and `grainId` for identity and delegates payload type handling to the configured serializer.

The <xref:Orleans.ILifecycleParticipant`1.Participate*?displayProperty=nameWithType> method subscribes to the silo's lifecycle.

Before starting the implementation, create an options class containing the root directory where grain state files are persisted. Create an options file named `FileGrainStorageOptions` containing the following:

:::code source="snippets/custom-grain-storage/FileGrainStorageOptions.cs" id="file_grain_storage_options":::

With the options class created, explore the constructor parameters of the `FileGrainStorage` class:

- `storageName`: Specifies which grains should use this storage provider through <xref:Orleans.Providers.StorageProviderAttribute>, for example, `[StorageProvider(ProviderName = "File")]`.
- `options`: The options class just created.
- `clusterOptions`: The cluster options used for retrieving the <xref:Orleans.Configuration.ClusterOptions.ServiceId>.

## Initialize the storage

To initialize the storage, subscribe to the <xref:Orleans.ServiceLifecycleStage.ApplicationServices?displayProperty=nameWithType> stage with an `onStart` function. Consider the following <xref:Orleans.ILifecycleParticipant`1.Participate*?displayProperty=nameWithType> implementation:

:::code source="snippets/custom-grain-storage/FileGrainStorage.cs" id="participate":::

The `onStart` function conditionally creates the root directory to store grain states if it doesn't already exist.

Also, provide a common function to construct the filename, ensuring uniqueness per service, grain ID, and state name:

:::code source="snippets/custom-grain-storage/FileGrainStorage.cs" id="getkeystring":::

## Read state

To read a grain state, get the filename using the `GetKeyString` function and combine it with the root directory from the `_options` instance.

:::code source="snippets/custom-grain-storage/FileGrainStorage.cs" id="readstateasync":::

Use `fileInfo.LastWriteTimeUtc` as an `ETag`, which other functions use for inconsistency checks to prevent data loss. Set <xref:Orleans.IGrainState`1.RecordExists> to indicate whether the read found a record.

For deserialization, use the <xref:Orleans.Storage.IStorageProviderSerializerOptions.GrainStorageSerializer?displayProperty=nameWithType>. This is important for correctly serializing and deserializing the state.

## Write state

Writing the state is similar to reading the state.

:::code source="snippets/custom-grain-storage/FileGrainStorage.cs" id="writestateasync":::

Similar to reading state, use the <xref:Orleans.Storage.IStorageProviderSerializerOptions.GrainStorageSerializer?displayProperty=nameWithType> to write the state. The current `ETag` checks against the file's last updated UTC time. If the date differs, it means another activation of the same grain changed the state concurrently. In this situation, throw an <xref:Orleans.Storage.InconsistentStateException>. This results in the current activation being killed to prevent overwriting the state previously saved by the other activated grain.

## Clear state

Clearing the state involves deleting the file if it exists.

:::code source="snippets/custom-grain-storage/FileGrainStorage.cs" id="clearstateasync":::

For the same reason as <xref:Orleans.Grain`1.WriteStateAsync*>, check for inconsistency. Before deleting the file and resetting the `ETag`, check if the current `ETag` matches the last write time UTC.

## Put it all together

Next, create a factory that allows scoping the options to the provider name while creating an instance of `FileGrainStorage` to ease registration with the service collection.

:::code source="snippets/custom-grain-storage/FileGrainStorageFactory.cs" id="file_grain_storage_factory":::

Lastly, to register the grain storage, create an extension on <xref:Orleans.Hosting.ISiloBuilder>. This extension registers the grain storage as a keyed singleton using <xref:Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddKeyedSingleton*?displayProperty=nameWithType>.

:::code source="snippets/custom-grain-storage/FileSiloBuilderExtensions.cs" id="file_silo_builder_extensions":::

The `FileGrainStorage` implements <xref:Orleans.Storage.IGrainStorage> and <xref:Orleans.ILifecycleParticipant`1> for <xref:Orleans.Runtime.ISiloLifecycle>. Therefore, register two keyed singleton services, one for each interface.

:::code source="snippets/custom-grain-storage/FileSiloBuilderExtensions.cs" id="KeyedRegistrations":::

This enables adding the file storage using the extension on <xref:Orleans.Hosting.ISiloBuilder>:

:::code source="snippets/custom-grain-storage/Program.cs" id="custom_grain_storage_program":::

Now you can select the provider using <xref:Orleans.Providers.StorageProviderAttribute>, for example, `[StorageProvider(ProviderName = "File")]`, and it stores the grain state in the root directory set in the options. Consider the full implementation of `FileGrainStorage`:

:::code source="snippets/custom-grain-storage/FileGrainStorage.cs" id="file_grain_storage":::
