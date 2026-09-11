---
title: Provider authoring architecture
description: Design Orleans providers with named services, configuration binding, validation, lifecycle, and runtime extension contracts.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Provider authoring architecture

An Orleans provider adapts an external system or alternate runtime implementation to a defined contract. Storage, clustering, reminders, grain directories, and persistent streams have different data-plane interfaces, but their hosting architecture follows the same pattern, built on the [.NET options pattern](https://learn.microsoft.com/dotnet/core/extensions/options) and [dependency injection](https://learn.microsoft.com/dotnet/core/extensions/dependency-injection/overview):

1. bind a named configuration section;
1. register named services and options;
1. validate configuration during host startup;
1. participate in lifecycle when resources need initialization; and
1. surface failures instead of silently degrading to another backend.

## Configuration-driven provider builders

<xref:Orleans.Providers.IProviderBuilder`1> is the bridge from Orleans configuration to a silo or client builder:

Provider packages associate a provider type string and category with a builder using assembly metadata. The host selects that builder from configuration, passes the provider name and section, and lets the builder call the same public registration API used by code-first configuration.

The Azure Queue stream implementation demonstrates this pattern: <xref:Orleans.Hosting.AzureQueueStreamProviderBuilder> implements builders for both <xref:Orleans.Hosting.ISiloBuilder> and <xref:Orleans.Hosting.IClientBuilder>. See its [implementation](https://github.com/dotnet/orleans/blob/main/src/Azure/Orleans.Streaming.AzureStorage/Hosting/AzureQueueStreamProviderBuilder.cs).

## Named-service composition

Many provider kinds allow multiple instances. The provider name is therefore part of service identity, options identity, logging identity, and runtime lookup. <xref:Orleans.Hosting.NamedServiceConfigurator> and its derived configurators register keyed components and named options without creating a private service provider.

A registration extension should:

- require a non-empty name when the provider category is named;
- register options through `AddOptions<T>(name)` or an Orleans configurator;
- register the contract and implementation under the same key;
- use <xref:Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAdd*> only for truly shared defaults; and
- include the name in validators and diagnostics.

Resolving an unkeyed singleton for a named component can make the first provider's options leak into every other provider.

## Separate control plane and data plane

The provider builder and options are the control plane. The runtime contract is the data plane:

| Provider kind | Data-plane contract |
| --- | --- |
| Cluster membership | <xref:Orleans.IMembershipTable> |
| Grain storage | <xref:Orleans.Storage.IGrainStorage> |
| Grain directory | <xref:Orleans.GrainDirectory.IGrainDirectory> |
| Reminder table | <xref:Orleans.IReminderTable> |
| Persistent streams | <xref:Orleans.Streams.IQueueAdapterFactory> and its adapter components |

Do not let configuration concerns weaken the data-plane contract. For example, a membership provider must preserve conditional updates and ordered versions regardless of whether credentials came from a connection string, a keyed SDK client, or managed identity.

## Validation

Options validation should fail before the silo joins the cluster or starts accepting traffic. Orleans providers commonly register an <xref:Orleans.IConfigurationValidator> so validation can include named options and service dependencies which ordinary data-annotation validation cannot express.

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

The persistent stream provider illustrates staged lifecycle composition: it creates the adapter during initialization, starts pulling agents at the active stage, then stops agents before closing. See <xref:Orleans.Providers.Streams.Common.PersistentStreamProvider.Participate*?displayProperty=nameWithType> and its [implementation](https://github.com/dotnet/orleans/blob/main/src/Orleans.Streaming/PersistentStreams/PersistentStreamProvider.cs).

Membership callers use the `Async`-suffixed, cancellation-aware methods of <xref:Orleans.IMembershipTable> for initialization, reads, and writes. Built-in providers forward tokens to backend APIs which support cancellation and bound waits on tokenless SDK operations while observing late faults. Custom providers can implement these methods directly; their default implementations adapt existing tokenless providers by canceling the caller's wait while the operation completes. The original tokenless method names remain as obsolete compatibility entry points. Cancellation can race with a committed write, so conditional writes and table versions continue to govern subsequent updates.

Return completed backend results, and observe cancellation before starting further I/O. Mapping an already-returned result preserves that operation's outcome.

Shared membership refreshes live until the membership manager is disposed. Each caller owns its wait, while periodic maintenance and its queued cleanup requests stop with the silo lifecycle. This keeps membership reads available during shutdown.

Membership table RPCs retain their existing operation aliases and application-argument payloads, with cancellation propagated separately. The original generated request types remain available for calls through obsolete tokenless methods. During rolling upgrades, each receiver uses its implementation's cancellation behavior.

When a lifecycle callback must execute its cancellation or cleanup logic, schedule it with <xref:System.Threading.Tasks.Task.Run*> and pass the cancellation token to the operation inside the callback. The callback then owns how cancellation completes its work.

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
