---
title: Orleans contract compatibility analyzer
description: Track grain RPC contracts during development to identify changes which can break rolling upgrades.
ms.date: 08/28/2026
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

Projects which use `Microsoft.Orleans.Sdk`, `Microsoft.Orleans.Client`, or `Microsoft.Orleans.Server` already receive the Orleans analyzers through those packages. A project which references `Microsoft.Orleans.Analyzers` directly can use the same property. Scope this property to projects which own Orleans contracts. In a large repository, set it in those projects or in a shared props file for their subtree instead of enabling contract analysis at the repository root.

To promote every contract diagnostic, configure the standard `Versioning` category:

```ini
[*.cs]
dotnet_analyzer_diagnostic.category-Versioning.severity = error
```

This also promotes informational diagnostics such as `ORLEANS0020`. Configure `dotnet_diagnostic.ORLEANS####.severity` entries instead when only selected contract diagnostics should change severity.

## Configure the manifest path

By default, the analyzer tracks `OrleansContracts.txt` beside the project file. During design-time builds used by IDEs and `dotnet format`, the analyzer package registers the configured path as a compiler `AdditionalFile` before the file exists, allowing regeneration to create it. Regular builds register an existing manifest and report `ORLEANS0020` when the configured manifest is absent. No explicit `AdditionalFiles` item or seed file is required.

Set `OrleansContractsPath` to use another location or filename:

```xml
<PropertyGroup>
  <EnableOrleansContractsAnalyzer>true</EnableOrleansContractsAnalyzer>
  <OrleansContractsPath>$(MSBuildProjectDirectory)/contracts/rpc-contracts.txt</OrleansContractsPath>
</PropertyGroup>
```

The path can also be set in `Directory.Build.props` to apply a repository convention. Each project needs its own manifest because the analyzer evaluates the contracts compiled into that project.

## Create and update the manifest

After opting in, build the project. If no manifest exists, diagnostic `ORLEANS0020` identifies the missing file at the first contract declaration. Apply **Regenerate OrleansContracts.txt** to create and populate the manifest for the project.

The regeneration code fix rebuilds every active interface, method, and grain-class entry from the project compilation. It preserves existing `*RETIRED*` entries and marks declarations which are no longer in source as retired. The generated header, line endings, and ordinal entry order are deterministic.

### Regenerate the manifest

Apply **Regenerate OrleansContracts.txt** from `ORLEANS0016`, `ORLEANS0017`, `ORLEANS0018`, `ORLEANS0019`, `ORLEANS0020`, `ORLEANS0022`, `ORLEANS0023`, or `ORLEANS0024`. One application regenerates the entire project manifest. In an IDE, use **Fix all in project**.

`ORLEANS0027` intentionally retains a removed method signature, so regeneration isn't offered for that diagnostic. If it is the only remaining diagnostic, restore the source method or explicitly delete the retained signature after reviewing and accepting the wire-compatibility break.

Agents and command-line workflows can regenerate manifests without an IDE:

```dotnetcli
dotnet format PATH_TO_PROJECT.csproj analyzers --severity info --diagnostics ORLEANS0016 ORLEANS0017 ORLEANS0018 ORLEANS0019 ORLEANS0020 ORLEANS0022 ORLEANS0023 ORLEANS0024
```

Run the command from the repository root. Replace `PATH_TO_PROJECT.csproj` with the owning project path to regenerate one manifest. The `--severity info` option includes `ORLEANS0020`, allowing the command to create the default manifest or a configured `OrleansContractsPath`, including its parent directory.

Run the command once for each project which owns an Orleans contract manifest. Do not pass a `.sln` or `.slnx` path: `dotnet format` analyzes the complete solution before applying Fix All, which can consume substantial time and memory in large repositories. Automation can invoke the project command for each contract project with bounded parallelism.

Regeneration edits `OrleansContracts.txt` files only. Source `[Alias]`, `[Id]`, `[GrainType]`, and `[GrainInterfaceType]` attributes remain unchanged.

Every method line places the effective wire identity before a colon, followed by the CLR method name and signature. The identity is the source `[Id]` value, source `[Alias]` value, or the generated xxHash32 method ID already used by the Orleans code generator. The stable identity appears first so contract-breaking changes are prominent in diffs, while CLR-only renames keep the same leading value.

After the command completes:

1. Inspect `git diff -- "*OrleansContracts.txt"` and account for every changed identity, version, and method signature.
2. Preserve all `*RETIRED*` declarations and retained removed-method signatures unless the compatibility break is intentional.
3. Run `dotnet build PATH_TO_PROJECT.csproj` and resolve all Orleans contract diagnostics. `ORLEANS0027` remains until a removed method is restored or its retained signature is explicitly deleted after compatibility review.

Add the generated file to source control and review its diff before committing. Treat every changed contract line as a potential wire-compatibility change:

- A changed `GrainInterfaceType`, `GrainType`, method identity, parameter type, or return type changes a wire identity or signature.
- A removed source contract becomes `*RETIRED*`, preserving its identity history and preventing accidental reuse.
- A removed RPC method remains in the manifest and reports `ORLEANS0027` until the method is restored or the wire break is explicitly accepted by removing the retained signature.
- A `[Version]` change affects version-aware routing and must align with the rolling-upgrade design.
- A changed CLR method name with an unchanged identity records a refactor while the Orleans wire contract remains stable.

Coding agents should regenerate the manifest instead of hand-editing active entries, retain retired history, and explain the compatibility impact of each contract diff in the change description.

## Manifest format

Interface methods are indented beneath their interface:

```text
# This file is generated by the Orleans contract analyzer.
# To regenerate this project from the repository root:
# dotnet format PATH_TO_PROJECT.csproj analyzers --severity info --diagnostics ORLEANS0016 ORLEANS0017 ORLEANS0018 ORLEANS0019 ORLEANS0020 ORLEANS0022 ORLEANS0023 ORLEANS0024
# Run the command once per contract project; do not pass a .sln or .slnx path.
# Verify with: dotnet build PATH_TO_PROJECT.csproj
# The regeneration command edits this manifest only; it does not change source attributes.
# OrleansContracts format: 2
# Method lines use: wire-identity: CLR-signature.
# The identity is the identifier Orleans uses at runtime, whether generated or declared in source.
# Review every diff: identity or signature changes can break wire compatibility during rolling upgrades.
# Details: https://aka.ms/orleans/OrleansContracts.txt

interface [GrainInterfaceType("Contoso.Grains.ICartGrain")] Contoso.Grains.ICartGrain [Version(1)]
  15793847: AddAsync(Contoso.Grains.Item) -> Task
  857AC6B2: GetAsync() -> Task<Contoso.Grains.Cart>

class [GrainType("cart")] Contoso.Grains.CartGrain
```

Each declaration includes both its Orleans identity and CLR type name. A diff which changes the Orleans identity changes the wire contract. A diff which changes only the CLR type name preserves an explicit Orleans identity.

Explicit identities remain visible alongside their CLR names:

```text
# Contoso.Grains.ICartGrain
interface [GrainInterfaceType("cart")] Contoso.Grains.ICartGrain [Version(1)]
  add: AddAsync(Contoso.Grains.Item) -> Task

# Contoso.Grains.CartGrain
class [GrainType("cart")] Contoso.Grains.CartGrain
```

The value before the colon is the effective runtime identity. A source `[Id(42)]`, source `[Alias("42")]`, and generated method ID `42` describe the same wire identity. The manifest records that result as `42:` and keeps source provenance in source code. Syntax-sensitive characters in aliases are backslash-escaped.

`*RETIRED*` marks an intentionally removed contract:

```text
*RETIRED* interface [GrainInterfaceType("Contoso.Grains.ILegacyGrain")] Contoso.Grains.ILegacyGrain [Version(0)]

*RETIRED* class [GrainType("legacy")] Contoso.Grains.LegacyGrain
```

Retired entries preserve contract history and prevent a removed identity from being unintentionally reused.

## Refactor-safe identities

The analyzer uses Orleans identities before CLR names:

- <xref:Orleans.GrainTypeAttribute> identifies grain classes.
- <xref:Orleans.Runtime.GrainInterfaceTypeAttribute> identifies grain interfaces.
- <xref:Orleans.IdAttribute> or <xref:Orleans.AliasAttribute> identifies grain methods.
- <xref:Orleans.AliasAttribute> identifies serialized parameter and return types.

When these identities remain unchanged, a CLR class, interface, method, parameter, or aliased data type can be renamed while preserving the wire identity. Changing an Orleans identity produces a contract diff and diagnostic.

Without an explicit stable identity, Orleans derives the identity from the CLR type name. Renaming the CLR type therefore changes the derived identity and the contract.

## Diagnostics

| Diagnostic | Default severity | Meaning |
| --- | --- | --- |
| [`ORLEANS0016`](../../diagnostics/orleans0016.md) | Warning | A grain interface has no active manifest declaration. |
| [`ORLEANS0017`](../../diagnostics/orleans0017.md) | Warning | The interface version differs from the manifest. |
| [`ORLEANS0018`](../../diagnostics/orleans0018.md) | Warning | An RPC method signature isn't declared. |
| [`ORLEANS0019`](../../diagnostics/orleans0019.md) | Warning | A removed interface isn't marked `*RETIRED*`. |
| [`ORLEANS0020`](../../diagnostics/orleans0020.md) | Info | The opted-in project has no manifest. |
| [`ORLEANS0021`](../../diagnostics/orleans0021.md) | Warning | An interface is declared more than once. |
| [`ORLEANS0022`](../../diagnostics/orleans0022.md) | Warning | A concrete grain class has no active declaration. |
| [`ORLEANS0023`](../../diagnostics/orleans0023.md) | Warning | A grain class identity differs from the manifest. |
| [`ORLEANS0024`](../../diagnostics/orleans0024.md) | Warning | A removed grain class isn't marked `*RETIRED*`. |
| [`ORLEANS0025`](../../diagnostics/orleans0025.md) | Warning | A grain class is declared more than once. |
| [`ORLEANS0027`](../../diagnostics/orleans0027.md) | Warning | An RPC method remains in the manifest after it is removed from source. |

Standard `.editorconfig` diagnostic configuration can change these severities. Prefer fixing contract drift instead of suppressing diagnostics.

## Scope

The analyzer tracks RPC interface signatures, numeric interface versions, and concrete grain class identities. Behavioral compatibility and persisted state schemas require separate review using the [backward compatibility guidelines](backward-compatibility-guidelines.md) and mixed-version deployment tests.
