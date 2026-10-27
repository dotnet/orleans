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

A source <xref:Orleans.AliasAttribute>, <xref:Orleans.GrainTypeAttribute>, or <xref:Orleans.Runtime.GrainInterfaceTypeAttribute> specifies a null or empty identity, a value containing a quote, whitespace, a control character, or a reserved type-name delimiter (`[](),*&`), or an invalid generic arity suffix.

## How to fix

Choose a non-empty identity containing no quotes, whitespace, control characters, or reserved type-name delimiters. Qualified names, nested type names, generic arity suffixes, and literal backslashes retain their meaning. Orleans records valid identities verbatim in the [contract manifest](../grains/grain-versioning/contract-compatibility-analyzer.md).

Write generic arity as a backtick followed by ASCII decimal digits at the end of each nested type name, totaling at most 64. For example, ``counter`1+Nested`2`` is valid; ``counter`1v2`` has text after its arity suffix and must be renamed.

Review identity changes for wire compatibility before deploying them. Attribute constructors and configured type-alias registration enforce the same validity rules at runtime. Follow the [identity migration guidance](../migration/contract-identities.md) for existing manifests and persisted aliases.
