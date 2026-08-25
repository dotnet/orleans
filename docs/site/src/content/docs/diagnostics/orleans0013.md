---
title: "ORLEANS0013: Remove serialization identity attributes from a grain class"
description: Understand and resolve ORLEANS0013 when a grain implementation is marked for data serialization.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0013: Remove serialization identity attributes from a grain class

| Property | Value |
| --- | --- |
| Category | Usage |
| Severity | Error |
| Code fix | Available |

## Cause

A class deriving from <xref:Orleans.Grain>, including `Grain<TState>`, is marked `[Alias]` or `[GenerateSerializer]`.

## Impact

Grain implementation instances are activation objects, not application payloads. Serializing the implementation introduces an incorrect data contract and can create alias conflicts.

## How to fix

Remove `[Alias]` and `[GenerateSerializer]` from the grain implementation. Apply serialization attributes to grain state and message types instead.

The code fix removes the attribute list containing the invalid attribute. Review the diff when the same attribute list contains unrelated attributes.

## Suppress the diagnostic

Suppress only for an unconventional integration which deliberately serializes the class independently of Orleans activation.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0013.severity = none
```
