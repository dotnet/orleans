---
title: Serialization customization in Orleans
description: Learn how to customize serialization in .NET Orleans.
ms.date: 07/03/2024
uid: orleans-serialization-customization
---

# Serialization customization in Orleans

One important aspect of Orleans is its support for customization of serialization, which is the process of converting an object or data structure into a format that can be stored or transmitted, and reconstructed later. This allows developers to control how data is encoded and decoded when it is sent between different parts of the system. Serialization customization can be useful for optimizing performance, interoperability, and security.

## Serialization providers

Orleans provides two serializer implementations:

- [Microsoft.Orleans.Serialization.SystemTextJson](https://nuget.org/packages/Microsoft.Orleans.Serialization.SystemTextJson)
- [Microsoft.Orleans.Serialization.NewtonsoftJson](https://nuget.org/packages/Microsoft.Orleans.Serialization.NewtonsoftJson)

To configure either of these packages, see [Serialization configuration in Orleans](serialization-configuration.md).

## Custom serializer implementation

To create a custom serializer implementation, there are a few common steps involved. You have to implement several interfaces and then register your serializer with the Orleans runtime. The following sections describe the steps in more detail.

Start by implementing the following Orleans serialization interfaces:

- <xref:Orleans.Serialization.Serializers.IGeneralizedCodec>: A codec which supports multiple types.
- <xref:Orleans.Serialization.Cloning.IGeneralizedCopier>: Provides functionality for copying objects of multiple types.
- <xref:Orleans.Serialization.ITypeFilter>: Functionality for allowing types to be loaded and to participate in serialization and deserialization.

Consider the following example of a custom serializer implementation:

:::code language="csharp" source="../../snippets/compiled/Host/HostSnippets.cs" id="custom_serializer":::

In the preceding example implementation:

- Each interface is explicitly implemented to avoid conflicts with method name resolution.
- Each method throws a <xref:System.NotImplementedException> to indicate that the method is not implemented. You'll need to implement each method to provide the desired functionality.

The next step is to register your serializer with the Orleans runtime. This is typically achieved by extending <xref:Orleans.Serialization.ISerializerBuilder> and exposing a custom `AddCustomSerializer` extension method. The following example demonstrates the typical pattern:

:::code language="csharp" source="../../snippets/compiled/Host/SerializationRegistrationSnippets.cs" id="register_custom_serializer":::

Additional considerations would be to expose an overload that accepts custom serialization options specific to your custom implementation. These options could be configured along with the registration in the builder. These options could be dependency injected into your custom serializer implementation.
