---
title: Orleans source generation
description: Understand build-time code generation for grains and serialization in Orleans 10.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Orleans source generation

Orleans 10 generates grain proxies, method dispatch code, serializers, and copiers at build time. There is no runtime or initialization-time code generation workflow for application code.

## Reference the SDK

Use the package matching the project role:

- `Microsoft.Orleans.Client` for client applications.
- `Microsoft.Orleans.Server` for silo applications.
- `Microsoft.Orleans.Sdk` for class libraries containing grain contracts, implementations, or serializable types.

These packages include the source generator and analyzers.

## Grain contracts

The generator discovers grain interfaces and implementations from their Orleans base interfaces. It reports build diagnostics for unsupported signatures, inaccessible types, multiple cancellation token parameters, and other contract errors.

Supported grain method return types are `Task`, `Task<T>`, `ValueTask`, and `ValueTask<T>`. The generator emits strongly typed references and invocation classes for those methods.

## Serializable types

Mark application data crossing grain boundaries or stored by Orleans with <xref:Orleans.GenerateSerializerAttribute>. Give serialized members stable field IDs:

```csharp
[GenerateSerializer]
public sealed class PurchaseOrder
{
    [Id(0)]
    public required string OrderId { get; init; }

    [Id(1)]
    public decimal Total { get; init; }
}
```

IDs are part of the wire and storage contract. Don't reuse or renumber them after deployment. Use <xref:Orleans.AliasAttribute> when a stable serialized type alias is required independently of the CLR name.

## Generate code for external types

When a project must generate serializers for accessible types declared elsewhere, use <xref:Orleans.GenerateCodeForDeclaringAssemblyAttribute>:

```csharp
[assembly: GenerateCodeForDeclaringAssembly(
    typeof(ExternalContract))]
```

Prefer owning serialization annotations with the type whenever possible. Generating for external declaring assemblies broadens the compatibility surface and can increase build output.

## Inspect diagnostics and output

Treat Orleans analyzer and generator diagnostics as contract errors, not warnings to suppress. Generated files can be inspected through normal compiler-generated-file tooling when debugging, but application code should depend on the public interfaces rather than generated implementation names.

For serialization rules and version tolerance, see the Orleans serialization documentation. For the runtime invocation pipeline, see the advanced implementation documentation.
