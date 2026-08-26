---
title: "ORLEANS0004: Add missing serialization attributes"
description: Understand and resolve ORLEANS0004 when a generated serializer type has unannotated members.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0004: Add missing serialization attributes

| Property | Value |
| --- | --- |
| Category | Usage |
| Severity | Error |
| Code fix | Available |

## Cause

A class, struct, or record marked `[GenerateSerializer]` has an instance field or auto-property without `[Id]` or `[NonSerialized]`.

## Impact

Unmarked members can be omitted from Orleans serialization, producing default or lost values and an incomplete version-tolerant schema.

## How to fix

Assign stable, unique `[Id(n)]` values to serialized members. Mark members which are intentionally excluded with `[NonSerialized]`.

The code fix can add sequential IDs after the highest existing ID or mark all unannotated members as non-serialized. Review generated IDs before committing them and never renumber deployed fields.

## Suppress the diagnostic

Prefer `[NonSerialized]` for intentional exclusion. Suppress only for test fixtures or legacy types whose omission is explicitly required.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0004.severity = none
```
