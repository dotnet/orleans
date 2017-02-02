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

We currently include three log-consistency providers (for state storage, log storage, and custom storage). All three are contained in the `Microsoft.Orleans.EventSourcing` package as well. Therefore, all Journaled Grains already have access to those. For a description of what these providers do and how they differ, see [Included Log-Consistency Providers](LogConsistencyProviders.md).

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
using Orleans.Runtime.Configuration; \\ pick up the necessary extension methods

config.AddLogStorageBasedLogConsistencyProvider("LogStorage");
config.AddStateStorageBasedLogConsistencyProvider("StateStorage");
config.AddCustomStorageBasedLogConsistencyProvider("CustomStorage");
```

## Grain Class Attributes

Each journaled grain class can be configured to use a log-consistency provider, and a storage provider of choice. These choices are independent.


### LogConsistencyProvider Attributes

By default, a journaled grain uses the built-in `StateStorage` provider. This provider persists the latest state snapshot, but not the events themselves. We can choose a different provider by adding a `[LogConsistencyProvider(ProviderName=...)]` attribute to the grain class, and give the name of the provider as configured by the Cluster Configuration. For example, the following attribute means that for `ChatGrain` instances, we use the `LogStorage` provider, which was configured to use `Orleans.EventSourcing.LogStorage.LogConsistencyProvider`. This provider always persists the entire event sequence (log).

```csharp
[LogConsistencyProvider(ProviderName = "LogStorage")]
public class ChatGrain : JournaledGrain<XDocument, IChatEvent>, IChatGrain { ... }
```

### StorageProvider Attributes

Some consistency providers (including both the LogStorage and StateStorage providers) in turn rely on a StorageProvider to communicate with storage. As usual, a default storage provider is used if none is explicitly specified. But we can add one. For example, we could specify

```csharp
[LogConsistencyProvider(ProviderName = "LogStorage")]
[StorageProvider(ProviderName = "AzureBlobStorage")]
public class ChatGrain : JournaledGrain<XDocument, IChatEvent>, IChatGrain { ... }
```


