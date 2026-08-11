---
title: Serialization configuration in Orleans
description: Learn how to configure serialization in .NET Orleans.
ms.date: 08/10/2026
ms.topic: how-to
uid: orleans-serialization-configuration
---

# Serialization configuration in Orleans

Serialization configuration in Orleans is a crucial part of the overall system design. While Orleans provides reasonable defaults, you can configure serialization to suit your app's needs. For sending data between hosts, <xref:Orleans.Serialization?displayProperty=fullName> supports delegating to other serializers, such as [Newtonsoft.Json](https://www.nuget.org/packages/Newtonsoft.Json) and [System.Text.Json](https://www.nuget.org/packages/System.Text.Json). You can add support for other serializers by following the pattern set by those implementations. For grain storage, it's best to use <xref:Orleans.Storage.IGrainStorageSerializer> to configure a custom serializer.

For the security implications of type resolution and serialized input, see [Serialization safety in Security in Orleans](../../security/index.md#serialization-safety).

## Configure Orleans to use `Newtonsoft.Json`

To configure Orleans to serialize certain types using `Newtonsoft.Json`, first reference the [Microsoft.Orleans.Serialization.NewtonsoftJson](https://nuget.org/packages/Microsoft.Orleans.Serialization.NewtonsoftJson) NuGet package. Then, configure the serializer, specifying which types it will be responsible for. In the following example, we specify that the `Newtonsoft.Json` serializer is responsible for all types in the `Example.Namespace` namespace.

``` csharp
siloBuilder.Services.AddSerializer(serializerBuilder =>
{
    serializerBuilder.AddNewtonsoftJsonSerializer(
        isSupported: type => type.Namespace.StartsWith("Example.Namespace"));
});
```

In the preceding example, the call to <xref:Orleans.Serialization.SerializationHostingExtensions.AddNewtonsoftJsonSerializer*> adds support for serializing and deserializing values using `Newtonsoft.Json.JsonSerializer`. You must perform similar configuration on all clients that need to handle those types.

For types marked with <xref:Orleans.GenerateSerializerAttribute>, Orleans prefers the generated serializer over the `Newtonsoft.Json` serializer.

## Configure Orleans to use `System.Text.Json`

Alternatively, to configure Orleans to use `System.Text.Json` to serialize your types, reference the [Microsoft.Orleans.Serialization.SystemTextJson](https://nuget.org/packages/Microsoft.Orleans.Serialization.SystemTextJson) NuGet package. Then, configure the serializer, specifying which types it will be responsible for. In the following example, we specify that the `System.Text.Json` serializer is responsible for all types in the `Example.Namespace` namespace.

- Install the [Microsoft.Orleans.Serialization.SystemTextJson](https://nuget.org/packages/Microsoft.Orleans.Serialization.SystemTextJson) NuGet package.
- Configure the serializer using the <xref:Orleans.Serialization.SerializationHostingExtensions.AddJsonSerializer*> method.

Consider the following example when interacting with the <xref:Orleans.Hosting.ISiloBuilder>:

```csharp
siloBuilder.Services.AddSerializer(serializerBuilder =>
{
    serializerBuilder.AddJsonSerializer(
        isSupported: type => type.Namespace.StartsWith("Example.Namespace"));
});
```

## Authorize type-name resolution

Registering an external serializer selects which codec can handle a value. It doesn't, by itself, authorize Orleans to resolve every CLR type name accepted by that serializer. Type-name resolution is a separate security boundary, and <xref:Orleans.Serialization.Configuration.TypeManifestOptions.AllowAllTypes?displayProperty=nameWithType> defaults to `false`.

This distinction is especially visible for polymorphic signatures such as `IReadOnlyList<TriggerRule>`, where `TriggerRule` is abstract and values are handled by `System.Text.Json`. Register the JSON serializer and explicitly trust the application type:

```csharp
siloBuilder.Services.AddSerializer(serializerBuilder =>
{
    serializerBuilder.AddJsonSerializer(
        isSupported: type => type.Namespace?.StartsWith("MyApp") == true);
    serializerBuilder.Configure(options =>
        options.AddAllowedType(typeof(TriggerRule)));
});
```

<xref:Orleans.Serialization.Configuration.TypeManifestOptions.AddAllowedType*> uses Orleans' runtime type-name formatter, including for constructed and nested generic types. The <xref:Orleans.Serialization.Configuration.TypeManifestOptions.AllowedTypes> string set remains supported for compatibility and contains Orleans-formatted runtime type names. Prefer `AddAllowedType` instead of constructing those names manually.

If every type in an application assembly is trusted, allow the assembly instead:

```csharp
siloBuilder.Services.AddSerializer(serializerBuilder =>
{
    serializerBuilder.Configure(options =>
        options.AddAllowedAssembly(typeof(TriggerRule).Assembly));
});
```

Assembly trust applies component by component. Allowing a generic type definition's assembly doesn't implicitly trust generic arguments from other assemblies.

For policy-based trust, register <xref:Orleans.Serialization.ITypeNameFilter> to evaluate names before Orleans loads the corresponding type:

```csharp
public sealed class ApplicationTypeNameFilter : ITypeNameFilter
{
    public bool? IsTypeNameAllowed(string typeName, string assemblyName)
    {
        if (assemblyName == "MyApp.Contracts"
            || assemblyName.StartsWith("MyApp.Contracts,", StringComparison.Ordinal))
        {
            return true;
        }

        return null;
    }
}

siloBuilder.Services.AddSingleton<ITypeNameFilter, ApplicationTypeNameFilter>();
```

A filter returns `true` to allow, `false` to deny, or `null` when it has no opinion. Types explicitly added to `AllowedTypes` are authoritative. For other names, a denial from any `ITypeNameFilter` takes precedence over other type-name filters and assembly trust. <xref:Orleans.Serialization.ITypeFilter> provides a resolved-`Type` fallback when name-based checks have no affirmative result; a denial wins within that fallback. Both formatting and parsing apply these checks, including to constructed generic components and array element types.

As a compatibility escape hatch, you can disable the boundary:

```csharp
siloBuilder.Services.AddSerializer(serializerBuilder =>
{
    serializerBuilder.Configure(options => options.AllowAllTypes = true);
});
```

> [!WARNING]
> `AllowAllTypes` bypasses type-name validation, including custom filters, and permits any resolvable type. Use it only when serialized input is fully trusted. Prefer allowing individual types or trusted assemblies.
