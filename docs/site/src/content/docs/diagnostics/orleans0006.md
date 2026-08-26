---
title: "ORLEANS0006: Static or abstract members cannot be serialized"
description: Understand and resolve ORLEANS0006 when an invalid member is assigned an Orleans serialization ID.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0006: Static or abstract members cannot be serialized

| Property | Value |
| --- | --- |
| Category | Usage |
| Severity | Error |
| Code fix | Not available |

## Cause

A static or abstract member in a `[GenerateSerializer]` type is marked with `[Id]`.

## Impact

The member has no concrete per-instance storage which Orleans can serialize, so the declared schema is invalid.

## How to fix

Remove `[Id]` from the static or abstract member. Serialize a concrete instance member instead, or make an abstract property concrete when it represents serialized state.

## Suppress the diagnostic

Suppress only for a verified analyzer or compiler mismatch.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0006.severity = none
```
