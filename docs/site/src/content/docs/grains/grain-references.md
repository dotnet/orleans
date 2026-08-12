---
title: Grain references
description: Create, use, and reason about grain references in Orleans.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Grain references

A grain reference is a generated proxy that implements a grain interface and addresses one logical grain. It contains the grain's [identity](grain-identity.md) and the interface used to call it, but it doesn't contain the activation's network location.

References remain valid when Orleans deactivates, recreates, or migrates the target activation. Getting a reference is a local operation and doesn't activate the grain.

## Get a reference

Use <xref:Orleans.IGrainFactory.GetGrain*> from a client, hosted service, or grain:

:::code language="csharp" source="../snippets/compiled/Grains/GrainSnippets.cs" id="counter_interface":::
:::code language="csharp" source="../snippets/compiled/Grains/GrainSnippets.cs" id="get_counter_reference":::

Within a class deriving from <xref:Orleans.Grain>, use its `GrainFactory` property. In other services, inject <xref:Orleans.IGrainFactory> or <xref:Orleans.IClusterClient>.

Grain references are serializable. They can be passed as grain method arguments, returned from calls, and included in persistent state.

## Interface-to-type resolution

The interface and key are usually enough for Orleans to identify the grain type. If exactly one grain class implements the interface, Orleans maps the interface to that class.

When multiple classes implement the same interface, prefer distinct marker interfaces:

:::code language="csharp" source="../snippets/compiled/Grains/GrainSnippets.cs" id="marker_interfaces":::

:::code language="csharp" source="../snippets/compiled/Grains/GrainSnippets.cs" id="get_marker_references":::

The two references have the same key but different grain types, so they address different logical grains.

For compatibility scenarios, <xref:Orleans.Metadata.DefaultGrainTypeAttribute> can select the default implementation for an interface, and `GetGrain` overloads accepting a grain class name prefix can disambiguate implementations. Explicit marker interfaces are usually clearer and safer to refactor.

## Cast a reference to another supported interface

If the same grain implementation supports another grain interface or extension interface, use <xref:Orleans.GrainExtensions.AsReference*>:

:::code language="csharp" source="../snippets/compiled/Grains/GrainSnippets.cs" id="cast_grain_reference":::

This doesn't create a different grain. It creates another typed proxy for the same grain identity. The target grain type must support the requested interface.

Within a grain, pass a reference to itself instead of passing `this`:

:::code language="csharp" source="../snippets/compiled/Grains/GrainSnippets.cs" id="self_reference":::

## Reference equality and storage

Multiple proxy objects can refer to the same grain. Compare logical identity when identity matters rather than relying on CLR object identity. Persist references only when retaining the interface relationship is useful; persist domain keys when the application should resolve the current interface at use time.

## Advanced: resolve from GrainId

Some `GetGrain` overloads accept <xref:Orleans.Runtime.GrainId>. These are intended for framework and infrastructure code that already has a resolved grain type and encoded key. Most application code should use a typed key overload because it preserves compile-time key and interface checks.
