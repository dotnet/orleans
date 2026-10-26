---
title: "ORLEANS0028: Invalid Orleans identity"
description: Resolve invalid characters in Orleans aliases, grain types, and grain interface types.
ms.date: 10/26/2026
ms.topic: reference
---

# ORLEANS0028: Invalid Orleans identity

| Property | Value |
| --- | --- |
| Category | Usage |
| Severity | Error |
| Code fix | Not available |

## Cause

A source <xref:Orleans.AliasAttribute>, <xref:Orleans.GrainTypeAttribute>, or <xref:Orleans.Runtime.GrainInterfaceTypeAttribute> specifies a null or empty identity, or a value containing a quote, whitespace, a control character, or a reserved type-name delimiter (`[](),*&`).

## How to fix

Choose a non-empty identity containing no quotes, whitespace, control characters, or reserved type-name delimiters. Qualified names, nested type names, generic arity suffixes, and literal backslashes retain their meaning. Orleans records valid identities verbatim in the [contract manifest](../grains/grain-versioning/contract-compatibility-analyzer.md).

Review identity changes for wire compatibility before deploying them. Attribute constructors and configured type-alias registration enforce the same validity rules at runtime.
