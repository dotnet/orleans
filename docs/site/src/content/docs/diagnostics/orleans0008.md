---
title: "ORLEANS0008: Grain interfaces cannot contain properties"
description: Understand and resolve ORLEANS0008 when an Orleans remote interface declares an instance property.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0008: Grain interfaces cannot contain properties

| Property | Value |
| --- | --- |
| Category | Usage |
| Severity | Error |
| Code fix | Not available |

## Cause

An interface used as an Orleans remote contract declares an instance property.

## Impact

Properties do not define supported explicit Orleans RPC operations and obscure the asynchronous remote-call boundary.

## How to fix

Replace the property with asynchronous get or set methods which use supported grain-call return types.

## Suppress the diagnostic

Suppress only when an interface inherits Orleans addressability but is proven never to be remoted.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0008.severity = none
```
