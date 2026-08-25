---
title: "ORLEANS0009: Use a registered grain-call return type"
description: Understand and resolve ORLEANS0009 when Orleans cannot map a grain interface method return type.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0009: Use a registered grain-call return type

| Property | Value |
| --- | --- |
| Category | Usage |
| Severity | Error |
| Code fix | Not available |

## Cause

An Orleans remote-interface method has no valid invokable-base-type mapping for its return type and selected proxy base.

## Impact

Orleans cannot generate and bind the invocation object and proxy method for the remote call.

## How to fix

Use a registered return type such as `Task`, `Task<T>`, `ValueTask`, or `ValueTask<T>`. For a custom calling abstraction, register a compatible `InvokableBaseType` and any required `ReturnValueProxy` initializer for every selected proxy base.

Malformed custom mappings report `ORLEANS0026`.

## Suppress the diagnostic

Suppress only when a valid custom runtime registration is intentionally invisible to the analyzer. Validate generated proxy and invokable code independently.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0009.severity = none
```

See [Customize Orleans code generation](../grains/code-generation-customization.md).
