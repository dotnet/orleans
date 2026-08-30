---
title: "ORLEANS0020: OrleansContracts.txt is missing"
description: Understand and resolve ORLEANS0020 when contract compatibility analysis is enabled without a manifest.
ms.date: 08/27/2026
ms.topic: reference
---

# ORLEANS0020: OrleansContracts.txt is missing

| Property | Value |
| --- | --- |
| Category | Versioning |
| Severity | Info |
| Code fix | Available |

## Cause

Contract compatibility analysis is enabled, the project contains tracked Orleans contracts, and the configured `OrleansContracts.txt` file was not found.

## Impact

The analyzer has no baseline, so it cannot detect RPC identity, signature, version, or grain-class changes.

## How to fix

Apply **Regenerate OrleansContracts.txt** to create and populate the complete project manifest. The configured path can be absent; the code fix creates the file and its parent directory. For a large solution, use the `Microsoft.Orleans.ContractTool` tool to regenerate every enabled project through a filtered workspace. Add the generated files to source control and review the baseline using the [contract compatibility guidance](../grains/grain-versioning/contract-compatibility-analyzer.md#regenerate-the-manifest).

## Suppress the diagnostic

If the project does not need contract tracking, disable the analyzer. Otherwise suppression defeats the project opt-in.

```xml
<EnableOrleansContractsAnalyzer>false</EnableOrleansContractsAnalyzer>
```

See [Orleans contract compatibility analyzer](../grains/grain-versioning/contract-compatibility-analyzer.md).
