---
title: "ORLEANS0007: Use one Orleans activation constructor"
description: Understand the legacy ORLEANS0007 constructor-selection diagnostic.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0007: Use one Orleans activation constructor

| Property | Value |
| --- | --- |
| Category | Usage |
| Severity | Error |
| Code fix | Not available |

## Cause

This legacy rule represents ambiguous constructor selection for an Orleans-serialized type. Current Orleans activation uses at most one constructor selected for generated activation.

## Impact

Ambiguous constructor metadata prevents deterministic object activation. The older `[OrleansConstructor]` marker is obsolete and is not used by current Orleans activation.

## How to fix

Remove obsolete or duplicate constructor markers. When generated activation requires an explicit constructor, use one `[GeneratedActivatorConstructor]`.

If this diagnostic appears because constructor declarations carry invalid `[GenerateSerializer]` attributes, remove those constructor-level attributes.

## Suppress the diagnostic

Suppress only for a known compatibility or test artifact after all constructor annotations have been reviewed.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0007.severity = none
```
