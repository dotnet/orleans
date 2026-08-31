---
title: "ORLEANS0017: Grain interface version mismatch"
description: Understand and resolve ORLEANS0017 when a grain interface version differs from OrleansContracts.txt.
ms.date: 08/27/2026
ms.topic: reference
---

# ORLEANS0017: Grain interface version mismatch

| Property | Value |
| --- | --- |
| Category | Versioning |
| Severity | Warning |
| Code fix | Available |

## Cause

The interface's `[Version]` value differs from the version recorded in `OrleansContracts.txt`. An interface without `[Version]` has version `0`.

## Impact

The manifest no longer describes the numeric version used by runtime compatibility and routing decisions. A version number change also does not make an incompatible method or payload change compatible by itself.

## How to fix

Determine whether the source or manifest changed unintentionally. Restore the previous source version, or review the rolling-upgrade implications and apply **Update version in OrleansContracts.txt** when the new version is intentional.

Apply **Regenerate OrleansContracts.txt** to rebuild the complete project manifest. Run regeneration separately for each contract project and review the generated diff using the [contract compatibility guidance](../grains/grain-versioning/contract-compatibility-analyzer.md#regenerate-the-manifest).

## Suppress the diagnostic

Use suppression only during a short-lived staged edit. Do not release with a source and manifest version mismatch.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0017.severity = none
```

See [Deploy new grain versions](../grains/grain-versioning/deploying-new-versions-of-grains.md).
