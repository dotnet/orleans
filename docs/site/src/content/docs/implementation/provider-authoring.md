---
title: Provider authoring architecture
description: Design Orleans providers with named services, configuration binding, validation, lifecycle, and runtime extension contracts.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Provider authoring architecture

An Orleans provider adapts an external system or alternate runtime implementation to a defined contract. Storage, clustering, reminders, grain directories, and persistent streams have different data-plane interfaces, but their hosting architecture follows the same pattern:

1. bind a named configuration section;
1. register named services and options;
1. validate configuration during host startup;
1. participate in lifecycle when resources need initialization; and
1. surface failures instead of silently degrading to another backend.

## Configuration-driven provider builders

<xref:Orleans.Providers.IProviderBuilder`1> is the bridge from Orleans configuration to a silo or client builder:

```csharp
public sealed class ExampleProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(
        ISiloBuilder builder,
        string? name,
        IConfigurationSection configurationSection)
    {
        builder.AddExampleProvider(
            name ?? throw new ArgumentNullException(nameof(name)),
            options => options.Bind(configurationSection));
    }
}
```

Provider packages associate a provider type string and category with a builder using assembly metadata. The host selects that builder from configuration, passes the provider name and section, and lets the builder call the same public registration API used by code-first configuration.

The Azure Queue stream implementation demonstrates this pattern: [`AzureQueueStreamProviderBuilder`](https://github.com/dotnet/orleans/blob/main/src/Azure/Orleans.Streaming.AzureStorage/Hosting/AzureQueueStreamProviderBuilder.cs) implements builders for both `ISiloBuilder` and `IClientBuilder`.

## Named-service composition

Many provider kinds allow multiple instances. The provider name is therefore part of service identity, options identity, logging identity, and runtime lookup. `NamedServiceConfigurator` and its derived configurators register keyed components and named options without creating a private service provider.

A registration extension should:

- require a non-empty name when the provider category is named;
- register options through `AddOptions<T>(name)` or an Orleans configurator;
- register the contract and implementation under the same key;
- use `TryAdd` only for truly shared defaults; and
- include the name in validators and diagnostics.

Resolving an unkeyed singleton for a named component can make the first provider's options leak into every other provider.

## Separate control plane and data plane

The provider builder and options are the control plane. The runtime contract is the data plane:

| Provider kind | Data-plane contract |
| --- | --- |
| Cluster membership | <xref:Orleans.IMembershipTable> |
| Grain storage | <xref:Orleans.Storage.IGrainStorage> |
| Grain directory | <xref:Orleans.GrainDirectory.IGrainDirectory> |
| Reminder table | <xref:Orleans.Runtime.IReminderTable> |
| Persistent streams | <xref:Orleans.Streams.IQueueAdapterFactory> and its adapter components |

Do not let configuration concerns weaken the data-plane contract. For example, a membership provider must preserve conditional updates and ordered versions regardless of whether credentials came from a connection string, a keyed SDK client, or managed identity.

## Validation

Options validation should fail before the silo joins the cluster or starts accepting traffic. Orleans providers commonly register an `IConfigurationValidator` so validation can include named options and service dependencies which ordinary data-annotation validation cannot express.

Validate at least:

- required endpoint, client, or credential source;
- mutually exclusive configuration forms;
- provider-specific naming and range constraints;
- compatibility between paired components; and
- capabilities required by the runtime contract.

Avoid broad exception handling which turns an inaccessible backend into an empty result. An empty membership table, missing grain state, or empty stream queue has domain meaning and must not represent a swallowed infrastructure failure.

## Lifecycle and ownership

Providers which allocate clients, receivers, leases, or background agents should implement or register an <xref:Orleans.ILifecycleParticipant`1>. Initialize after required runtime services are ready and stop before those services disappear.

Ownership must be explicit. If the application supplies a keyed SDK client, the provider generally should not dispose an object it does not own. If the provider creates receivers per queue, it should stop and dispose them when queue ownership moves.

The persistent stream provider illustrates staged lifecycle composition: it creates the adapter during initialization, starts pulling agents at the active stage, then stops agents before closing. See [`PersistentStreamProvider.Participate`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Streaming/PersistentStreams/PersistentStreamProvider.cs#L218-L235).

## Testing a provider

Contract tests should cover more than successful round trips:

- concurrent conditional updates and stale version rejection;
- duplicate registration or delivery behavior;
- cancellation and timeout propagation;
- startup validation;
- backend unavailability without silent fallback;
- resource cleanup after lifecycle stop;
- multiple named instances with isolated options; and
- rolling-upgrade compatibility of stored or transmitted data.

Use [TestingHost architecture](testing.md) to understand which runtime services a test cluster substitutes. Provider tests which depend on a real backend should state those preconditions and should not treat an emulator's weaker consistency as proof of the production contract.
