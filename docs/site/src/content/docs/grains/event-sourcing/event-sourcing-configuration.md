---
title: Event sourcing configuration
description: Learn about event sourcing configuration in .NET Orleans.
ms.date: 05/23/2025
ms.topic: how-to
---

# Event sourcing configuration

In this article, you learn about various event sourcing configuration options for .NET Orleans.

## Configure project references

### Grain interfaces

As before, interfaces depend only on the `Microsoft.Orleans.Core` package because the grain interface is independent of the implementation.

### Grain implementations

Journaled grains need to derive from <xref:Orleans.EventSourcing.JournaledGrain`2> or <xref:Orleans.EventSourcing.JournaledGrain`1>, which is defined in the `Microsoft.Orleans.EventSourcing` package.

### Log-consistency providers

We currently include three log-consistency providers (for state storage, log storage, and custom storage). All three are contained in the `Microsoft.Orleans.EventSourcing` package as well. Therefore, all journaled grains already have access to them. For a description of what these providers do and how they differ, see [Included log-consistency providers](log-consistency-providers.md).

## Cluster configuration

Configure log-consistency providers just like any other Orleans providers. For example, to include all three providers (though you probably won't need all three), add this to the `<Globals>` element of the configuration file:

```xml
<LogConsistencyProviders>
    <Provider Name="StateStorage"
        Type="Orleans.EventSourcing.StateStorage.LogConsistencyProvider" />
    <Provider Name="LogStorage"
        Type="Orleans.EventSourcing.LogStorage.LogConsistencyProvider" />
    <Provider Name="CustomStorage"
        Type="Orleans.EventSourcing.CustomStorage.LogConsistencyProvider" />
</LogConsistencyProviders>
```

You can achieve the same programmatically. Starting with Orleans 2.0.0 stable, `ClientConfiguration` and `ClusterConfiguration` no longer exist. They have been replaced by <xref:Orleans.ClientBuilder> and `SiloBuilder` (note there's no cluster builder).

```csharp
builder.AddLogStorageBasedLogConsistencyProvider("LogStorage")
```

## Grain class attributes

Each journaled grain class must have a <xref:Orleans.Providers.LogConsistencyProviderAttribute> to specify the log-consistency provider. Some providers additionally require a <xref:Orleans.Providers.StorageProviderAttribute>, for example:

```csharp
[StorageProvider(ProviderName = "OrleansLocalStorage")]
[LogConsistencyProvider(ProviderName = "LogStorage")]
public class EventSourcedBankAccountGrain :
    JournaledGrain<BankAccountState>, IEventSourcedBankAccountGrain
{
    //...
}
```

So here, `"OrleansLocalStorage"` is used for storing the grain state, whereas `"LogStorage"` is the in-memory storage provider for EventSourcing events.

### `LogConsistencyProvider` attributes

To specify the log-consistency provider, add a `[LogConsistencyProvider(ProviderName=...)]` attribute to the grain class and provide the name of the provider as configured in the cluster configuration, for example:

```csharp
[LogConsistencyProvider(ProviderName = "CustomStorage")]
public class ChatGrain :
    JournaledGrain<XDocument, IChatEvent>, IChatGrain, ICustomStorage
{
    // ...
}
```

### <xref:Orleans.Providers.StorageProviderAttribute> attributes

Some log-consistency providers (including `LogStorage` and `StateStorage`) use a standard <xref:Orleans.Providers.StorageProviderAttribute> to communicate with storage. Specify this provider using a separate <xref:Orleans.Providers.StorageProviderAttribute> attribute, as follows:

```csharp
[LogConsistencyProvider(ProviderName = "LogStorage")]
[StorageProvider(ProviderName = "AzureBlobStorage")]
public class ChatGrain :
    JournaledGrain<XDocument, IChatEvent>, IChatGrain
{
    // ...
}
```

## Default providers

You can omit the `LogConsistencyProvider` and/or <xref:Orleans.Providers.StorageProviderAttribute> attributes if a default is specified in the configuration. Do this by using the special name `Default` for the respective provider. For example:

```xml
<LogConsistencyProviders>
    <Provider Name="Default"
        Type="Orleans.EventSourcing.LogStorage.LogConsistencyProvider"/>
</LogConsistencyProviders>
<StorageProviders>
    <Provider Name="Default"
        Type="Orleans.Storage.MemoryStorage" />
</StorageProviders>
```
