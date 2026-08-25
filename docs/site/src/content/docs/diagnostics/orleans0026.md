---
title: "ORLEANS0026: Invalid invokable base type mapping"
description: Understand and resolve ORLEANS0026 when custom grain-call return type mappings cannot generate a valid invokable request.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0026: Invalid invokable base type mapping

| Property | Value |
| --- | --- |
| Category | Usage |
| Severity | Error |
| Code fix | Not available |

## Cause

An applicable custom `InvokableBaseType` registration is invalid for the grain method's return type and selected proxy base. Common causes include conflicting registrations, invalid generic arity or constraints, an inaccessible or sealed request base, a missing supported constructor, or an invalid `ReturnValueProxy` initializer.

## Impact

Orleans cannot reliably select or generate the invokable request type for the RPC method. Proxy and request code generation cannot produce a valid custom calling contract.

## How to fix

Locate the registration named by the diagnostic, then:

1. Remove conflicting mappings and avoid replacing built-in proxy defaults through assembly registration.
2. Match open generic return and request types by arity and constraints.
3. Use an accessible, non-static, non-sealed request base with a supported constructor.
4. Correct the `ReturnValueProxy` initializer name and signature.
5. Validate every proxy base selected by `GenerateMethodSerializers`.

## Suppress the diagnostic

Suppression does not repair the source-generation contract, and the source generator can report the same condition separately. Suppress only for a proven analyzer-only false positive after independently validating generated output.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0026.severity = none
```

See [Customize Orleans code generation](../grains/code-generation-customization.md).
