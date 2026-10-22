---
title: "ORLEANS0029: Grain state data types require serialization support"
description: Enable strict compile-time checks for missing Orleans serialization support in grain state graphs.
ms.topic: reference
---

# ORLEANS0029: Grain state data types require serialization support

| Property | Value |
| --- | --- |
| Category | Usage |
| Severity | Warning, disabled by default |
| Code fix | Not available |

## Enable the diagnostic

For applications whose grain storage uses Orleans serialization or whose state participates in live activation migration, add this setting to `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0029.severity = warning
```

Enable this rule independently of the [RPC contract check](orleans0028.md). Storage providers can use JSON or another serializer, so Orleans serializer annotations are not a universal storage requirement. Live activation migration transfers the in-memory state of `Grain<TState>` and `IPersistentState<TState>` through Orleans serialization, independently of the storage format. That path requires an Orleans serializer even when storage uses JSON. See [Activation lifecycle and migration](../grains/grain-lifecycle.md#grain-migration).

## Cause

A state type or nested data type used by <xref:Orleans.Grain`1> or <xref:Orleans.Runtime.IPersistentState`1> has no statically declared Orleans serialization support.

The analyzer finds state through grain base classes, constructor-injected persistent-state services, and persistent-state fields or properties, including inherited and closed generic declarations. Repeated uses of the same state type on one grain produce one graph check. Ordinary grain fields, local helper methods, and dependency-injection services are excluded.

## Impact

With Orleans-serialized storage, a missing state codec can fail a read or write only after the affected state path is exercised. With live activation migration, it can fail during state dehydration or transfer. This check reports statically visible missing support during compilation.

## How to fix

Mark owned state DTOs with <xref:Orleans.GenerateSerializerAttribute> and assign stable <xref:Orleans.IdAttribute> values to their serialized members, or provide an explicitly registered codec or surrogate converter. Annotate the state DTO rather than the grain implementation.

The traversal and [static-analysis limits](orleans0028.md#static-analysis-boundary-and-suppression) match the RPC rule. Verify the actual configured storage serializer and exercise a storage round trip when runtime configuration supplies serialization support.

## Suppress the diagnostic

Leave this diagnostic disabled when neither storage nor live activation migration uses Orleans serialization for the state. Verify storage round trips and, when enabled, migration before suppressing individual reports. The attribute itself is one way to provide serialization support; a registered codec or surrogate converter is also valid.
