---
title: "ORLEANS0010: Add a stable Alias"
description: Understand and resolve ORLEANS0010 when an Orleans type or RPC method uses a name-derived identity.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0010: Add a stable Alias

| Property | Value |
| --- | --- |
| Category | Usage |
| Severity | Info |
| Code fix | Available |

## Cause

A grain interface, grain-interface method, or generated-serializer data type has no `[Alias]`.

## Impact

Orleans falls back to a name-derived identity. Renaming the CLR type or method can break serialized data, RPC compatibility, or rolling upgrades.

## How to fix

Add a stable, intentionally chosen alias and preserve it across CLR renames. The code fix generates a type alias from its namespace and nesting, or a method alias from its current method name.

Review generated aliases for overloaded methods because each overload needs a unique method identity.

## Suppress the diagnostic

Suppress only when compatibility across renames, deployments, and persisted data is explicitly irrelevant.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0010.severity = none
```
