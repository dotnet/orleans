---
title: "ORLEANS0016: Grain interface is not active in OrleansContracts.txt"
description: Understand and resolve ORLEANS0016 when a grain interface is missing or retired in the contract manifest.
ms.date: 08/27/2026
ms.topic: reference
---

# ORLEANS0016: Grain interface is not active in OrleansContracts.txt

| Property | Value |
| --- | --- |
| Category | Orleans.Versioning |
| Severity | Warning |
| Code fix | Available |

## Cause

The contract analyzer found a grain interface with no active declaration matching its `GrainInterfaceType` in `OrleansContracts.txt`. The matching declaration can also be present but marked `*RETIRED*`.

## Impact

The interface identity, version, and methods are absent from contract review. A rename or identity change can become a new RPC contract without an explicit manifest diff.

## How to fix

Verify the interface identity and version, then apply **Add to OrleansContracts.txt**. The code fix adds or reactivates the interface and records its ordinary instance methods.

If the interface was restored accidentally, remove it from source or introduce a separately named replacement instead of reusing a retired identity.

Apply **Regenerate OrleansContracts.txt** to rebuild the complete project manifest, or use **Fix all in solution** to update every affected project. Review the generated diff using the [contract compatibility guidance](../grains/grain-versioning/contract-compatibility-analyzer.md#regenerate-the-manifest).

## Suppress the diagnostic

Deployable RPC contracts should remain in the manifest. If the project intentionally does not maintain a contract manifest, disable the contract analyzer for the project instead of suppressing individual interfaces.

```xml
<EnableOrleansContractsAnalyzer>false</EnableOrleansContractsAnalyzer>
```

See [Orleans contract compatibility analyzer](../grains/grain-versioning/contract-compatibility-analyzer.md).
