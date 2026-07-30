---
title: Serialization configuration in Orleans
description: Learn how to configure serialization in .NET Orleans.
ms.date: 05/23/2025
ms.topic: how-to
uid: orleans-serialization-configuration
zone_pivot_groups: orleans-version
---

# Serialization configuration in Orleans

:::zone target="docs" pivot="orleans-10-0,orleans-9-0,orleans-8-0,orleans-7-0"

Serialization configuration in Orleans is a crucial part of the overall system design. While Orleans provides reasonable defaults, you can configure serialization to suit your app's needs. For sending data between hosts, <xref:Orleans.Serialization?displayProperty=fullName> supports delegating to other serializers, such as [Newtonsoft.Json](https://www.nuget.org/packages/Newtonsoft.Json) and [System.Text.Json](https://www.nuget.org/packages/System.Text.Json). You can add support for other serializers by following the pattern set by those implementations. For grain storage, it's best to use <xref:Orleans.Storage.IGrainStorageSerializer> to configure a custom serializer.

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

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

## External serializer providers

Ensure serialization configuration is identical on all clients and silos. Inconsistent configurations can lead to serialization errors.

Specify serialization providers implementing `IExternalSerializer` using the <xref:Orleans.Configuration.SerializationProviderOptions.SerializationProviders?displayProperty=nameWithType> property of <xref:Orleans.Runtime.Configuration.ClientConfiguration> and <xref:Orleans.Runtime.Configuration.GlobalConfiguration> in code:

```csharp
// Client configuration
var clientConfiguration = new ClientConfiguration();
clientConfiguration.SerializationProviders.Add(
    typeof(FantasticSerializer).GetTypeInfo());

// Global configuration
var globalConfiguration = new GlobalConfiguration();
globalConfiguration.SerializationProviders.Add(
    typeof(FantasticSerializer).GetTypeInfo());
```

Alternatively, specify them in XML configuration under the `<SerializationProviders />` property of `<Messaging>`:

```xml
<Messaging>
    <SerializationProviders>
        <Provider type="GreatCompany.FantasticSerializer, GreatCompany.SerializerAssembly" />
    </SerializationProviders>
</Messaging>
```

In both cases, you can configure multiple providers. The collection is ordered. This means if a provider capable of serializing types `A` and `B` is specified before a provider that can only serialize type `B`, the latter provider won't be used for type `B`.

:::zone-end

## See also

- [Serialization overview in Orleans](serialization.md)
- [Serialization customization in Orleans](serialization-customization.md)
