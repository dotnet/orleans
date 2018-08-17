---
layout: page
title: Configuration
---

# Configuration

## Configuring Project References

### Grain Interfaces

As before, interfaces depend only on the `Microsoft.Orleans.Core` package, because the grain interface is independent of the implementation. 

### Grain Implementations

JournaledGrains need to derive from `JournaledGrain<S,E>` or `JournaledGrain<S>`, which is defined in the `Microsoft.Orleans.EventSourcing` package. 

### Log-Consistency Providers

We currently include three log-consistency providers (for state storage, log storage, and custom storage). All three are contained in the `Microsoft.Orleans.EventSourcing` package as well. Therefore, all Journaled Grains already have access to those. For a description of what these providers do and how they differ, see [Included Log-Consistency Providers](log_consistency_providers.md).

## Cluster Configuration

Log-consistency providers are configured just like any other Orleans providers.
For example, to include all three providers (of course, you probably won't need all three), add this to the `<Globals>` element of the configuration file:

```xml
<LogConsistencyProviders>
  <Provider Type="Orleans.EventSourcing.StateStorage.LogConsistencyProvider" Name="StateStorage" />
  <Provider Type="Orleans.EventSourcing.LogStorage.LogConsistencyProvider" Name="LogStorage" />
  <Provider Type="Orleans.EventSourcing.CustomStorage.LogConsistencyProvider" Name="CustomStorage" />
</LogConsistencyProviders>
```
The same can be achieved programmatically. Assuming the project contains the `Microsoft.Orleans.EventSourcing` package, and `config` is a `ClusterConfiguration` object:

```csharp
using Orleans.Runtime.Configuration; // pick up the necessary extension methods

config.AddLogStorageBasedLogConsistencyProvider("LogStorage");
config.AddStateStorageBasedLogConsistencyProvider("StateStorage");
config.AddCustomStorageBasedLogConsistencyProvider("CustomStorage");
```

## Grain Class Attributes

Each journaled grain class must have a `LogConsistencyProvider` attribute to specify the log-consistency provider. Some providers additionally require a `StorageProvider` attribute.


### LogConsistencyProvider Attributes

To specify the log-consistency provider, add a `[LogConsistencyProvider(ProviderName=...)]` attribute to the grain class, and give the name of the provider as configured by the Cluster Configuration. For example:

```csharp
[LogConsistencyProvider(ProviderName = "CustomStorage")]
public class ChatGrain : JournaledGrain<XDocument, IChatEvent>, IChatGrain, ICustomStorage { ... }
```

### StorageProvider Attributes

Some log-consistency providers (including `LogStorage` and `StateStorage`) use a standard StorageProvider to communicate with storage. This provider is specified using a separate `StorageProvider` attribute, as follows:

```csharp
[LogConsistencyProvider(ProviderName = "LogStorage")]
[StorageProvider(ProviderName = "AzureBlobStorage")]
public class ChatGrain : JournaledGrain<XDocument, IChatEvent>, IChatGrain { ... }
```

## Default Providers

It is possible to omit the `LogConsistencyProvider` and/or the `StorageProvider` attributes, if a default is specified in the configuration. This is done by using the special name `Default` for the respective provider. For example:
```xml
<LogConsistencyProviders>
  <Provider Type="Orleans.EventSourcing.LogStorage.LogConsistencyProvider" Name="Default" />
</LogConsistencyProviders>
<StorageProviders>
  <Provider Type="Orleans.Storage.MemoryStorage" Name="Default" />
</StorageProviders>
```