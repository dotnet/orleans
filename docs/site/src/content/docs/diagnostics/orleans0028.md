---
title: "ORLEANS0028: Union cases can have the same type"
description: Understand and resolve ORLEANS0028 when two cases of a serializable union type are constructed from the same generic type.
ms.date: 10/12/2026
ms.topic: reference
---

# ORLEANS0028: Union cases can have the same type

| Property | Value |
| --- | --- |
| Category | Usage |
| Severity | Warning |
| Code fix | Not available |

## Cause

A union type with `[GenerateSerializer]` declares two cases which are constructed from the same generic type and differ only by type arguments, such as `Value<L>` and `Value<R>` in a union declared as `union Either<L, R>(Value<L>, Value<R>)`. When the type arguments are equal, both cases have the same type.

## Impact

A union value stores only its case value, so a value whose type matches both cases is indistinguishable from either case. Pattern matching selects the first matching case. Orleans serialization writes such a value as the first matching case, so it round-trips with the same value and the same pattern-matching result, and the case which originally created the value is not preserved.

## How to fix

Declare distinct case types, such as separate `Left<L>` and `Right<R>` types, when the case which created a value must be preserved. Keep the shared case type when first-case semantics are acceptable, and suppress the diagnostic for that union.

## Suppress the diagnostic

Suppress the diagnostic on the union declaration with `#pragma warning disable ORLEANS0028`, or disable it in configuration:

```ini
[*]
dotnet_diagnostic.ORLEANS0028.severity = none
```
