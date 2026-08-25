---
title: "ORLEANS0002: Reference parameter modifiers are not allowed"
description: Understand and resolve ORLEANS0002 when a grain interface method uses ref, out, or in parameters.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0002: Reference parameter modifiers are not allowed

| Property | Value |
| --- | --- |
| Category | Usage |
| Severity | Error |
| Code fix | Not available |

## Cause

An instance method on an Orleans grain, observer, extension, or system-target interface uses a `ref`, `out`, or `in` parameter.

## Impact

Orleans RPC serializes parameter values across process boundaries. It cannot provide local reference or write-back semantics for remote calls.

## How to fix

Pass ordinary values and return outputs through the asynchronous result. Use a result DTO or tuple when the method returns multiple values.

## Suppress the diagnostic

Suppress only for a verified false positive where the interface is never used as an Orleans remote contract.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0002.severity = none
```
