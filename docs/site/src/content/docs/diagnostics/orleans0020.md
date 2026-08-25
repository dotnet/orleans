---
title: "ORLEANS0020: OrleansContracts.txt is missing"
description: Understand and resolve ORLEANS0020 when contract compatibility analysis is enabled without a manifest.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0020: OrleansContracts.txt is missing

| Property | Value |
| --- | --- |
| Category | Orleans.Versioning |
| Severity | Info |
| Code fix | Not available |

## Cause

Contract compatibility analysis is enabled, the project contains tracked Orleans contracts, and the configured `OrleansContracts.txt` file was not found.

## Impact

The analyzer has no baseline, so it cannot detect RPC identity, signature, version, or grain-class changes.

## How to fix

Create `OrleansContracts.txt` at `OrleansContractsPath`, add it to source control, and rebuild. Apply the resulting diagnostics' code fixes to populate interface, method, and class declarations.

## Suppress the diagnostic

If the project does not need contract tracking, disable the analyzer. Otherwise suppression defeats the project opt-in.

```xml
<EnableOrleansContractsAnalyzer>false</EnableOrleansContractsAnalyzer>
```

See [Orleans contract compatibility analyzer](../grains/grain-versioning/contract-compatibility-analyzer.md).
