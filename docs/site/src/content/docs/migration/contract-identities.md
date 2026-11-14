---
title: Migrate Orleans contract identities
description: Migrate existing contract manifests and persisted aliases when adopting runtime-aligned identity analysis.
ms.date: 10/27/2026
ms.topic: how-to
---

# Migrate Orleans contract identities

The [contract compatibility analyzer](../grains/grain-versioning/contract-compatibility-analyzer.md) records the identity used by Orleans: an explicit <xref:Orleans.Runtime.GrainInterfaceTypeAttribute>, otherwise the interface's <xref:Orleans.AliasAttribute>, otherwise its CLR name with aliases substituted for containing types.

Orleans 10.4.0 generated CLR-based interface identities even for aliased interfaces. Upgrading to alias-aware analysis requires a one-time review of those checked-in `OrleansContracts.txt` entries. Identity validation also introduces a separate runtime compatibility requirement for applications with aliases containing newly rejected characters.

## Migrate a 10.4.0 contract manifest

Perform the baseline migration against the last deployed source and configuration, before changing application contracts:

1. Keep a copy of the deployed source, its contract manifests, and its identity configuration.
2. Resolve each affected interface's deployed identity using <xref:Orleans.Metadata.GrainInterfaceTypeResolver.GetGrainInterfaceType*>. Check explicit interface identities first, then interface and containing-type aliases. Review custom identity providers and configured type aliases against the application's runtime configuration.
3. For an entry which incorrectly records a CLR name, replace its recorded `GrainInterfaceType` value with the verified deployed identity. Preserve its CLR name, version, method identities, signatures, comments, and retirement status.
4. Build with the updated analyzer and review every remaining diagnostic. After the baseline matches the deployed identities, regenerate it using the [project-level regeneration command](../grains/grain-versioning/contract-compatibility-analyzer.md#regenerate-the-manifest) and review the diff.
5. Commit the baseline correction separately from application contract changes. Confirm that a subsequent alias change produces a contract diagnostic.

For example, if the deployed interface `Contoso.ICounterGrain` has the alias `counter`, correct this entry:

```text
interface [GrainInterfaceType("Contoso.ICounterGrain")] Contoso.ICounterGrain [Version(2)]
  ping: Ping() -> Task
```

to:

```text
interface [GrainInterfaceType("counter")] Contoso.ICounterGrain [Version(2)]
  ping: Ping() -> Task
```

This changes the recorded baseline to match the deployed wire identity. The runtime attributes and persisted data stay unchanged.

Regenerating an uncorrected 10.4.0 baseline emits an active alias-based entry and retains the old CLR-based entry as `*RETIRED*`. This preserves the recorded history conservatively. Review those pairs against the deployed source to distinguish baseline corrections from actual identity replacements. A current alias alone cannot establish which alias an earlier deployment used.

For legacy declarations which contain only a CLR name, regenerate while the original source types are available. The analyzer can then resolve nested types and record their identities in retired declarations. Recover the deployed source when a retired declaration's original nesting or alias history needs to be established.

## Migrate aliases rejected by identity validation

[`ORLEANS0028`](../diagnostics/orleans0028.md) validates <xref:Orleans.AliasAttribute>, <xref:Orleans.GrainTypeAttribute>, and <xref:Orleans.Runtime.GrainInterfaceTypeAttribute> declarations. The same rules apply when constructing these attributes and registering configured type aliases:

- Use a non-empty value containing no quotes, whitespace, control characters, or reserved type-name delimiters (`[](),*&`).
- Write generic arity as a backtick followed by ASCII decimal digits at the end of each nested type name, with a combined arity of at most 64. For example, ``Contoso.Outer`1+Inner`2`` has a combined arity of three.

For an application with an affected persisted alias, complete its data migration using a compatible Orleans version before adopting the validation change:

1. Inventory source identities and configured aliases, including names used by stored grain state, stream payloads, and persisted grain references. Back up the affected data.
2. Choose valid replacement identities. Use an application-specific migration running on the earlier Orleans version to read the old payloads and rewrite them with the replacement identities. Preserve field IDs and application state, and account for grain keys, references, and storage addressing when changing a grain type.
3. Verify that the upgraded application reads every migrated payload and can construct its serializer and runtime services. Establish a rollback build which can read the rewritten data.
4. Coordinate the deployment and traffic cutover for changed RPC or grain identities using the [deployment and rollback guidance](deployment-and-rollback.md).

Renaming a source attribute changes future identities; rewriting existing payloads establishes their compatibility with the new identity. Registering a rejected name in `WellKnownTypeAliases` also triggers runtime validation, so finish the old-format reads with the earlier runtime.
