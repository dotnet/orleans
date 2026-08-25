---
title: "ORLEANS0012: Change a duplicated serialization Id"
description: Understand and resolve ORLEANS0012 when serialized members reuse an Orleans field ID.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0012: Change a duplicated serialization Id

| Property | Value |
| --- | --- |
| Category | Usage |
| Severity | Error |
| Code fix | Available |

## Cause

Two or more members of one `[GenerateSerializer]` type use the same constant `[Id]` value.

## Impact

Field-ID ambiguity can deserialize data into the wrong member, corrupt payloads, and break version tolerance.

## How to fix

Restore the established field-ID mapping. Assign a previously unused ID only to the new or incorrect member, and never renumber IDs which have been deployed.

The code fix selects a value greater than the highest ID found in the document. Review the result carefully before applying Fix All.

## Suppress the diagnostic

Do not suppress this diagnostic for a type serialized by Orleans. Remove the serialization annotations if the type is not part of an Orleans schema.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0012.severity = none
```
