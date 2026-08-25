---
title: "ORLEANS0011: Rename a duplicated Alias"
description: Understand and resolve ORLEANS0011 when Orleans aliases collide.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0011: Rename a duplicated Alias

| Property | Value |
| --- | --- |
| Category | Usage |
| Severity | Error |
| Code fix | Partially available |

## Cause

Different types use the same non-empty `[Alias]`, or multiple methods in one grain interface use the same alias.

## Impact

The collision creates ambiguous type registration, serialization identity, or RPC method dispatch.

## How to fix

Give each serialized type a globally unique stable alias and each grain-interface method a unique alias within its interface.

The code fix can rename later duplicate method aliases with a numeric suffix. Type alias collisions require a deliberate manual rename.

## Suppress the diagnostic

Do not suppress a real collision. Suppress only a proven analyzer-scope false positive after confirming that the identities cannot coexist.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0011.severity = none
```
