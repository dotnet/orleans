---
title: "ORLEANS0005: Add GenerateSerializer"
description: Understand and resolve ORLEANS0005 when a Serializable type does not opt into Orleans source-generated serialization.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0005: Add GenerateSerializer

| Property | Value |
| --- | --- |
| Category | Usage |
| Severity | Info |
| Code fix | Available |

## Cause

A type has `[Serializable]` but does not have `[GenerateSerializer]`.

## Impact

The type does not use Orleans source-generated serialization and can miss Orleans' version-tolerant schema and generated-code performance.

## How to fix

Apply the code fix to add `[GenerateSerializer]`, then address any `ORLEANS0004` diagnostics by assigning stable member IDs or excluding members.

## Suppress the diagnostic

Suppress when the type is never serialized by Orleans or deliberately uses a custom or external serializer.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0005.severity = none
```
