---
title: Orleans source generation
description: Understand build-time code generation for grains and serialization in Orleans.
ms.date: 08/07/2026
ms.topic: concept-article
---

# Orleans source generation

Orleans generates grain proxies, method dispatch code, serializers, and copiers at build time. There is no runtime or initialization-time code generation workflow for application code.

## Reference the SDK

Use the package matching the project role:

- [Microsoft.Orleans.Client](https://www.nuget.org/packages/Microsoft.Orleans.Client) for client applications.
- [Microsoft.Orleans.Server](https://www.nuget.org/packages/Microsoft.Orleans.Server) for silo applications.
- [Microsoft.Orleans.Sdk](https://www.nuget.org/packages/Microsoft.Orleans.Sdk) for class libraries containing grain contracts, implementations, or serializable types.

These packages include the source generator and analyzers.

## Grain contracts

The generator discovers grain interfaces and implementations from their Orleans base interfaces. It reports build diagnostics for unsupported signatures, inaccessible types, multiple cancellation token parameters, and other contract errors.

Supported grain method return types are <xref:System.Threading.Tasks.Task>, <xref:System.Threading.Tasks.Task`1>, <xref:System.Threading.Tasks.ValueTask>, and <xref:System.Threading.Tasks.ValueTask`1>. The generator emits strongly typed references and invocation classes for those methods.

## Serializable types

Mark application data crossing grain boundaries or stored by Orleans with <xref:Orleans.GenerateSerializerAttribute>. Give serialized members stable field IDs:

:::code language="csharp" source="../snippets/compiled/Grains/GeneralSnippets.cs" id="serializable_purchase_order":::
IDs are part of the wire and storage contract. Don't reuse or renumber them after deployment. Use <xref:Orleans.AliasAttribute> when a stable serialized type alias is required independently of the CLR name.

## Generate code for external types

When a project must generate serializers for accessible types declared elsewhere, use <xref:Orleans.GenerateCodeForDeclaringAssemblyAttribute>:

:::code language="csharp" source="../snippets/compiled/Grains/GeneralSnippets.cs" id="generate_external_contract":::
Prefer owning serialization annotations with the type whenever possible. Generating for external declaring assemblies broadens the compatibility surface and can increase build output.

## Other .NET languages

For end-to-end interop examples, see the [Orleans F# sample](https://learn.microsoft.com/samples/dotnet/samples/orleans-fsharp-sample/) and [Orleans Visual Basic sample](https://learn.microsoft.com/samples/dotnet/samples/orleans-vb-sample/).

## Inspect diagnostics and output

Treat Orleans analyzer and generator diagnostics as contract errors, not warnings to suppress. Generated files can be inspected through normal compiler-generated-file tooling when debugging, but application code should depend on the public interfaces rather than generated implementation names.

The optional [Orleans contract compatibility analyzer](grain-versioning/contract-compatibility-analyzer.md) records grain RPC identities and signatures in `OrleansContracts.txt` so contract drift can be reviewed before a rolling upgrade.

Libraries which define custom grain-call return types can extend the generated proxy and invokable request model. See [Customize Orleans serialization code generation](code-generation-customization.md).

For serialization rules and version tolerance, see [Orleans serialization](../host/configuration-guide/serialization.md).
