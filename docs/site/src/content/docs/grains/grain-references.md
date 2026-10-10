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

## Owned RPC arguments

Apply <xref:Orleans.DisposeOnCompletionAttribute> to a grain interface parameter implementing <xref:System.IDisposable> when its copier acquires independent ownership. The generated proxy transfers its copied argument to the request, and a receiving request owns each decoded argument. The caller retains ownership of the original value. Unannotated parameters keep their existing copying and lifetime semantics.

Orleans retires owned arguments when a call completes, is rejected, is canceled, times out, or encounters host shutdown. An outgoing filter which short-circuits the call also retires its copied arguments. For one-way calls, successful transport writing retires the sending request; a rerouted write retains ownership for the next attempt.

Canceling a queued grain request removes it from the activation's queue and retires its owned arguments or unread receive buffer. For request-response calls, the activation sends a cancellation response after removing the request. Cleanup runs through that terminal path even if response sending fails.

Active serialization and invocation retain the arguments through their actual end. Terminal completion prevents new uses, and the last retained use releases each owned disposable exactly once. A grain method which continues after caller cancellation therefore keeps its decoded arguments valid until that invocation finishes. Code which keeps an argument beyond the invocation must acquire its own independent ownership.

Cleanup attempts every owned argument even when one disposal throws. Runtime cleanup failures produce warning logs while preserving the call's original outcome. Generated copy, decode, and submission failure paths use the serializer's registered logger; when a logger is unavailable, concurrent initialization and cleanup failures surface together in an aggregate exception. Direct method calls use the ownership assigned by their caller.

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
