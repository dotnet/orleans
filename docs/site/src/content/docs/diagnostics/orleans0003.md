---
title: "ORLEANS0003: Inherit from Grain (removed)"
description: Understand the removed ORLEANS0003 diagnostic reported by older Orleans analyzer packages.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0003: Inherit from Grain (removed)

| Property | Value |
| --- | --- |
| Category | Usage |
| Status | Removed |
| Code fix | Historical |

## Cause

Older Orleans analyzer packages reported this diagnostic when a non-abstract class implemented a grain interface without deriving from <xref:Orleans.Grain>.

## Impact

Current Orleans versions support POCO grain classes through <xref:Orleans.IGrainBase>. This diagnostic no longer represents a framework requirement.

## How to fix

Upgrade the Orleans analyzer package. A POCO grain can remain independent of <xref:Orleans.Grain> when it implements the current grain activation contract.

## Suppress the diagnostic

When upgrading is temporarily blocked, suppressing this removed rule is safe for an intentionally supported POCO grain.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0003.severity = none
```
