---
title: Serialization configuration in Orleans
description: Learn how to configure serialization in .NET Orleans.
ms.date: 08/10/2026
ms.topic: how-to
uid: orleans-serialization-configuration
---

# Serialization configuration in Orleans

Serialization configuration in Orleans is a crucial part of the overall system design. While Orleans provides reasonable defaults, you can configure serialization to suit your app's needs. For sending data between hosts, <xref:Orleans.Serialization?displayProperty=fullName> supports delegating to other serializers, such as [Newtonsoft.Json](https://www.nuget.org/packages/Newtonsoft.Json) and [System.Text.Json](https://www.nuget.org/packages/System.Text.Json). You can add support for other serializers by following the pattern set by those implementations. For grain storage, it's best to use <xref:Orleans.Storage.IGrainStorageSerializer> to configure a custom serializer.

## Configure Orleans to use `Newtonsoft.Json`

To configure Orleans to serialize certain types using `Newtonsoft.Json`, first reference the [Microsoft.Orleans.Serialization.NewtonsoftJson](https://nuget.org/packages/Microsoft.Orleans.Serialization.NewtonsoftJson) NuGet package. Then, configure the serializer, specifying which types it will be responsible for. In the following example, we specify that the `Newtonsoft.Json` serializer is responsible for all types in the `Example.Namespace` namespace.

:::code language="csharp" source="snippets/serialization/TypeNameResolutionExamples.cs" id="configure_newtonsoft_json":::

In the preceding example, the call to <xref:Orleans.Serialization.SerializationHostingExtensions.AddNewtonsoftJsonSerializer*> adds support for serializing and deserializing values using `Newtonsoft.Json.JsonSerializer`. You must perform similar configuration on all clients that need to handle those types.

For types marked with <xref:Orleans.GenerateSerializerAttribute>, Orleans prefers the generated serializer over the `Newtonsoft.Json` serializer.

## Configure Orleans to use `System.Text.Json`

Alternatively, to configure Orleans to use `System.Text.Json` to serialize your types, reference the [Microsoft.Orleans.Serialization.SystemTextJson](https://nuget.org/packages/Microsoft.Orleans.Serialization.SystemTextJson) NuGet package. Then, configure the serializer, specifying which types it will be responsible for. In the following example, we specify that the `System.Text.Json` serializer is responsible for all types in the `Example.Namespace` namespace.

- Install the [Microsoft.Orleans.Serialization.SystemTextJson](https://nuget.org/packages/Microsoft.Orleans.Serialization.SystemTextJson) NuGet package.
- Configure the serializer using the <xref:Orleans.Serialization.SerializationHostingExtensions.AddJsonSerializer*> method.

Consider the following example when interacting with the <xref:Orleans.Hosting.ISiloBuilder>:

:::code language="csharp" source="snippets/serialization/TypeNameResolutionExamples.cs" id="configure_system_text_json":::

## Authorize type-name resolution

Registering an external serializer selects which codec handles a value. Wire type-name resolution binds host-established CLR identities, then authorizes the complete type graph. <xref:Orleans.Serialization.Configuration.TypeManifestOptions.AllowAllTypes?displayProperty=nameWithType> defaults to `false`.

This distinction is especially visible for polymorphic signatures such as `IReadOnlyList<TriggerRule>`, where `TriggerRule` is abstract and values are handled by `System.Text.Json`. Register the JSON serializer and explicitly trust the application type:

:::code language="csharp" source="../../snippets/compiled/Host/HostSnippets.cs" id="allow_type":::

<xref:Orleans.Serialization.Configuration.TypeManifestOptions.AddAllowedType*> preserves the actual CLR identity and its constructed and nested generic components. The <xref:Orleans.Serialization.Configuration.TypeManifestOptions.AllowedTypes> string set remains supported for trusted host configuration: Orleans resolves those registrations during initialization and preserves the resulting identities. Prefer `AddAllowedType` when the type is available.

If every type in an application assembly is trusted, allow the assembly instead:

:::code language="csharp" source="../../snippets/compiled/Host/HostSnippets.cs" id="allow_assembly":::

Assembly registration captures the assembly's available type identities during host configuration. Trust applies component by component: the generic definition and each argument require permission. For trimming and NativeAOT, use individual type registrations and generated serializer contexts to preserve the required closed type graph. Ordinary application lookup resolves assembly-qualified names through the specified assembly, including CLR type forwarding.

For policy-based trust, register <xref:Orleans.Serialization.ITypeNameFilter> to evaluate complete wire names before binding them to registered identities:

:::code language="csharp" source="snippets/serialization/TypeNameResolutionExamples.cs" id="application_type_name_filter":::

Register the filter with dependency injection:

:::code language="csharp" source="snippets/serialization/TypeNameResolutionExamples.cs" id="register_type_name_filter":::

A filter returns `true` to allow, `false` to deny, or `null` when it has no opinion. Explicit individual type registrations are authoritative. On the wire path, name-filter denials take precedence over metadata grants, aliases, and assembly trust; resolved-type denials apply to the bound definition, arguments, and final construction. Wire readers cache filter opinions for bound host identities for the configuration's lifetime. Aliases supply identities, while serializer metadata and configured grants supply permission.

Ordinary <xref:Orleans.Serialization.TypeSystem.TypeConverter.Parse*> retains application lookup semantics, including an explicit resolved-type filter grant for a closed generic construction with no-opinion arguments. It inspects every argument for denials, including arguments after an unknown argument. Wire deserialization independently requires authorization of the definition and every argument.

For fully trusted inputs, `AllowAllTypes` grants permission to all host-known identities:

:::code language="csharp" source="snippets/serialization/TypeNameResolutionExamples.cs" id="allow_all_types":::

> [!WARNING]
> `AllowAllTypes` bypasses authorization, including custom filters. Keep individual type or trusted assembly registrations for less-trusted inputs.

### Migrate runtime-name contracts

Before upgrading a client or silo, register the concrete polymorphic types which its payloads name. Use `AddAllowedType` for each reviewed type, `AddAllowedAssembly` for a reviewed assembly, or a generated serializer context for a closed type graph. Keep compatible registrations on every process which reads the contract, including persisted state and queues.

Custom type converters and resolvers continue to support ordinary application lookup. For their wire contracts, register custom names in <xref:Orleans.Serialization.Configuration.TypeManifestOptions.WellKnownTypeAliases> with actual `Type` values and establish permission for those targets. Wire readers use these registrations directly. Treat missing identities as registration or version-skew errors, and deploy the receiving registrations before sending the new contracts.

Register reviewed custom exception factories using <xref:Orleans.Serialization.ExceptionSerializationOptions.AddExceptionType*>. The exception codec restores base exception properties using those factories. A well-formed unavailable exception preserves its scalar diagnostic state in <xref:Orleans.Serialization.UnavailableExceptionFallbackException> and discards its nested exception and data graphs. See [exception admission](../../security/serialization.md#admit-exception-reconstruction) for the reconstruction and fallback policy.

For the full trust-boundary and data-validation guidance, see [serialization security](../../security/serialization.md).
