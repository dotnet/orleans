---
title: Orleans contract compatibility analyzer
description: Track grain RPC contracts during development to identify changes which can break rolling upgrades.
ms.date: 08/11/2026
ms.topic: concept-article
---

# Orleans contract compatibility analyzer

The Orleans contract compatibility analyzer compares the grain interfaces and concrete grain classes in a project with a checked-in contract manifest. It helps reviewers identify changes to RPC identities and signatures which can break rolling upgrades.

The analyzer is **disabled by default**. Enable it explicitly in a project file or a shared `Directory.Build.props`:

```xml
<PropertyGroup>
  <EnableOrleansContractsAnalyzer>true</EnableOrleansContractsAnalyzer>
</PropertyGroup>
```

Projects which use `Microsoft.Orleans.Sdk`, `Microsoft.Orleans.Client`, or `Microsoft.Orleans.Server` already receive the Orleans analyzers through those packages. A project which references `Microsoft.Orleans.Analyzers` directly can use the same property.

## Configure the manifest path

By default, the analyzer looks for `OrleansContracts.txt` beside the project file. The analyzer package automatically adds an existing file at that location as a compiler `AdditionalFile`; no explicit `AdditionalFiles` item is required.

Set `OrleansContractsPath` to use another location or filename:

```xml
<PropertyGroup>
  <EnableOrleansContractsAnalyzer>true</EnableOrleansContractsAnalyzer>
  <OrleansContractsPath>$(MSBuildProjectDirectory)\contracts\rpc-contracts.txt</OrleansContractsPath>
</PropertyGroup>
```

The path can also be set in `Directory.Build.props` to apply a repository convention. Each project needs its own manifest because the analyzer evaluates the contracts compiled into that project.

## Create and update the manifest

After opting in, build the project. If no manifest exists, diagnostic `ORLEANS0020` identifies the missing file. Create the file, include it in source control, and apply the Orleans code fixes to add missing interface and class entries. Apply the code fixes again after adding RPC methods.

Code fixes preserve the file's line endings and write entries in stable ordinal order. The resulting file is deterministic regardless of the order in which fixes are applied.

## Manifest format

Interface methods are indented beneath their interface:

```text
interface Contoso.Grains.ICartGrain [Version(1)]
  AddAsync(Contoso.Grains.Item) -> Task
  GetAsync() -> Task<Contoso.Grains.Cart>

class Contoso.Grains.CartGrain
```

Stable Orleans identities are written when source code supplies them:

```text
# Contoso.Grains.ICartGrain
interface [GrainInterfaceType("cart")] Contoso.Grains.ICartGrain [Version(1)]
  # Contoso.Grains.ICartGrain.AddAsync(Item item) -> Task
  add(Contoso.Grains.Item) -> Task

# Contoso.Grains.CartGrain
class [GrainType("cart")] Contoso.Grains.CartGrain
```

Comments record CLR names only when they differ from the stable identity. Comments are informational and aren't part of contract matching.

`*RETIRED*` marks an intentionally removed contract:

```text
*RETIRED* interface Contoso.Grains.ILegacyGrain [Version(0)]

*RETIRED* class Contoso.Grains.LegacyGrain
```

Don't delete retired entries. They preserve the contract history and prevent a removed identity from being unintentionally reused.

## Refactor-safe identities

The analyzer uses Orleans identities before CLR names:

- <xref:Orleans.GrainTypeAttribute> identifies grain classes.
- <xref:Orleans.Runtime.GrainInterfaceTypeAttribute> identifies grain interfaces.
- <xref:Orleans.IdAttribute> or <xref:Orleans.AliasAttribute> identifies grain methods.
- <xref:Orleans.AliasAttribute> identifies serialized parameter and return types.

When these identities remain unchanged, renaming a CLR class, interface, method, parameter, or aliased data type doesn't require a manifest update. Changing an Orleans identity remains a contract change and produces a diagnostic.

Without an explicit stable identity, the CLR name is the identity. Renaming it therefore changes the contract.

## Diagnostics

| Diagnostic | Default severity | Meaning |
| --- | --- | --- |
| `ORLEANS0016` | Warning | A grain interface has no active manifest declaration. |
| `ORLEANS0017` | Warning | The interface version differs from the manifest. |
| `ORLEANS0018` | Warning | An RPC method signature isn't declared. |
| `ORLEANS0019` | Warning | A removed interface isn't marked `*RETIRED*`. |
| `ORLEANS0020` | Info | The opted-in project has no manifest. |
| `ORLEANS0021` | Warning | An interface is declared more than once. |
| `ORLEANS0022` | Warning | A concrete grain class has no active declaration. |
| `ORLEANS0023` | Warning | A grain class identity differs from the manifest. |
| `ORLEANS0024` | Warning | A removed grain class isn't marked `*RETIRED*`. |
| `ORLEANS0025` | Warning | A grain class is declared more than once. |

Standard `.editorconfig` diagnostic configuration can change these severities. Prefer fixing contract drift instead of suppressing diagnostics.

## Scope

The analyzer tracks RPC interface signatures, numeric interface versions, and concrete grain class identities. It doesn't prove behavioral compatibility or validate persisted state schemas. Continue to follow the [backward compatibility guidelines](backward-compatibility-guidelines.md) and test mixed-version deployments before production rollout.
