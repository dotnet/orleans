---
title: Archived Orleans 3.x to 7.x migration notes
description: Historical guidance for crossing the incompatible Orleans 3-to-7 identity, hosting, and serialization boundary.
ms.date: 08/08/2026
ms.topic: how-to
---

# Archived Orleans 3.x to 7.x migration notes

> [!WARNING]
> Orleans 3.x and Orleans 7.x are out of support. Use these notes only to reach a supported intermediate codebase, then continue through [Orleans 7.x to 10.x](7-to-10.md).

## Starting and target assumptions

These notes apply to an Orleans 3.x application that must first become an Orleans 7 application. This transition isn't wire compatible. Orleans 3 and Orleans 7 silos can't form a mixed cluster, so deploy Orleans 7 in a separate cluster and plan an application-specific state transition.

## Required architectural changes

- Reference [Microsoft.Orleans.Server](https://www.nuget.org/packages/Microsoft.Orleans.Server) from silo projects, [Microsoft.Orleans.Client](https://www.nuget.org/packages/Microsoft.Orleans.Client) from client projects, and [Microsoft.Orleans.Sdk](https://www.nuget.org/packages/Microsoft.Orleans.Sdk) from shared contract projects.
- Remove the legacy MSBuild code-generator and `Microsoft.Orleans.OrleansRuntime` packages.
- Replace `Microsoft.Orleans.OrleansServiceBus` with [Microsoft.Orleans.Streaming.EventHubs](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.EventHubs). Add explicit [Microsoft.Orleans.Reminders](https://www.nuget.org/packages/Microsoft.Orleans.Reminders) and [Microsoft.Orleans.Streaming](https://www.nuget.org/packages/Microsoft.Orleans.Streaming) references when the application uses those features.
- Remove Application Parts configuration. The Orleans source generator discovers application types.
- Use the [.NET generic host](https://learn.microsoft.com/dotnet/core/extensions/generic-host) with <xref:Microsoft.Extensions.Hosting.OrleansSiloGenericHostExtensions.UseOrleans*> and <xref:Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient*>.
- Update <xref:Orleans.Grain.OnActivateAsync*> and <xref:Orleans.Grain.OnDeactivateAsync*> overrides to the Orleans 7 cancellation-token and deactivation-reason signatures.
- Add <xref:Orleans.GenerateSerializerAttribute> and stable <xref:Orleans.IdAttribute> values to application types.
- Replace legacy grain, interface, and stream identity assumptions with the Orleans 7 string-based identity model.
- Replace Simple Message Streams with broadcast channels or a persistent stream provider.
- Replace legacy telemetry consumers with .NET metrics and <xref:System.Diagnostics.ActivitySource>-based tracing.

The old <xref:Orleans.Hosting.GrainCallFilterServiceCollectionExtensions.AddGrainCallFilter*> API was removed before Orleans 7. Register incoming and outgoing filters on <xref:Orleans.Hosting.ISiloBuilder> or <xref:Orleans.Hosting.IClientBuilder>.

For an itemized record of the Orleans samples migrated to Orleans 7, see [dotnet/orleans issue #8035](https://github.com/dotnet/orleans/issues/8035).

## Why this boundary requires conversion

Orleans 7 changed two durable contracts which Orleans 10 retains:

- Grain, grain-interface, and stream identities moved to the string-based type, namespace, and key representations used by <xref:Orleans.Runtime.GrainId> and <xref:Orleans.Runtime.StreamId>.
- The Orleans wire serializer was replaced by the version-tolerant serializer based on <xref:Orleans.GenerateSerializerAttribute>, <xref:Orleans.IdAttribute>, and <xref:Orleans.AliasAttribute>.

These changes make applications easier to evolve after the Orleans 7 checkpoint, but they don't make Orleans 3 identities or payloads automatically compatible. Define and test the conversion before the Orleans 7 cluster reads production data.

### Map identities and serialized state

Create an explicit mapping for every durable identity:

- Preserve the domain value of each GUID, integer, string, or compound grain key while mapping the old grain type to its Orleans 7-and-later type name. Use <xref:Orleans.GrainTypeAttribute> when that name must remain independent of the CLR class name.
- Map stream namespaces and GUID keys to stable <xref:Orleans.Runtime.StreamId> namespace and key strings. Keep provider names stable unless the migration deliberately moves subscriptions to another provider.
- Add stable serializer member IDs, and add aliases before renaming serialized types. Don't reuse IDs or aliases after the Orleans 7 checkpoint.
- Treat serialized grain references, queued messages, reminders, stream subscriptions, and provider metadata as part of the conversion, not only grain-state rows.

The version-tolerant serializer governs compatible changes after conversion. Don't assume that it can decode payloads written by the Orleans 3 wire serializer or by a storage serializer with different settings. Verify each stored type using representative production data. See [Grain identity](../grains/grain-identity.md) and [Orleans serialization](../host/configuration-guide/serialization.md) for the Orleans 10 contracts.

### Replace Simple Message Streams

Simple Message Streams were removed in Orleans 7. Choose the replacement based on the behavior the application relied on:

- Use [broadcast channels](../streaming/broadcast-channel.md) for nonpersistent, best-effort delivery to implicit subscribers. Broadcast channels don't retain events, replay messages, or support explicit subscriptions.
- Use an [Orleans stream provider](../streaming/index.md) when the application requires explicit subscriptions, durable subscription metadata, an external broker, replay, or provider-backed delivery.

Don't select a replacement only because both APIs deliver notifications. Record how each old provider name, namespace, key, and subscription maps to the new abstraction, then test duplicate, delayed, and lost-message behavior. See [Choose an Orleans messaging abstraction](../streaming/streams-why.md) for the current behavior matrix.

### Replace legacy telemetry consumers

The `Microsoft.Orleans.TelemetryConsumers.*` packages and legacy telemetry-consumer model were removed. Orleans 10 emits structured logs, a `Microsoft.Orleans` <xref:System.Diagnostics.Metrics.Meter>, and <xref:System.Diagnostics.ActivitySource> traces. Replace old consumers with the application's logging and OpenTelemetry pipeline, and register activity propagation on every participating silo and client. See [Orleans observability](../host/monitoring/index.md).

### Account for separated feature packages

Streaming, reminders, and transactions moved out of the core package. Add `Microsoft.Orleans.Streaming`, `Microsoft.Orleans.Reminders`, and `Microsoft.Orleans.Transactions` only where those features are used. APIs which previously appeared as <xref:Orleans.Grain> instance methods are extension methods in these packages, so unqualified calls might need `this.`, such as `this.GetReminders()` or `this.GetStreamProvider("provider")`.

## State and deployment boundary

Grain and stream identities and the wire serializer changed incompatibly in Orleans 7. Don't point a new cluster at production state until you have verified:

- How old grain identities map to new string identities.
- How each persisted payload is converted or read.
- How reminders and stream subscriptions are recreated or migrated.
- How traffic is cut over without two clusters processing the same logical entities.

Prefer an offline export/transform/import process or an application-level bridge with idempotent writes. Keep the Orleans 3 data recovery point until the Orleans 7 cluster has completed validation.

## Continue to Orleans 10

After the Orleans 7 application is stable:

1. Update it to the latest Orleans 7.2 patch.
1. Follow [Upgrade Orleans 7.x to 10.x](7-to-10.md).
1. Use a separate deployment checkpoint for Orleans 8.2, Orleans 9.2, and Orleans 10.x.

## Checklist

- [ ] Build an Orleans 7 codebase using current package and hosting patterns.
- [ ] Define grain, stream, reminder, and state identity mappings.
- [ ] Convert and validate representative persisted payloads.
- [ ] Deploy Orleans 7 in a separate cluster.
- [ ] Verify rollback to the Orleans 3 recovery point.
- [ ] Stabilize on Orleans 7.2 before continuing sequentially to Orleans 10.
