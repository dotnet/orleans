---
title: Grain identity
description: Understand grain types, keys, and GrainId values in Orleans 10.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Grain identity

Every grain has a stable logical identity made from:

1. A **grain type**, which identifies the implementation.
1. A **grain key**, which identifies one logical grain within that type.

Orleans represents the complete identity with <xref:Orleans.Runtime.GrainId>. A `GrainId` exposes `Type` and `Key`; it doesn't expose a `PrimaryKey` property. Application code normally uses typed [grain references](grain-references.md) and key helpers rather than constructing `GrainId` values directly.

## Choose a key type

Declare the key shape on the grain interface:

| Interface | Key supplied to `GetGrain` |
|---|---|
| <xref:Orleans.IGrainWithStringKey> | `string` |
| <xref:Orleans.IGrainWithGuidKey> | <xref:System.Guid> |
| <xref:Orleans.IGrainWithIntegerKey> | `long` |
| <xref:Orleans.IGrainWithGuidCompoundKey> | <xref:System.Guid> plus a string extension |
| <xref:Orleans.IGrainWithIntegerCompoundKey> | `long` plus a string extension |

Choose keys from the application's domain, such as a customer ID, device ID, or account number. Grain keys are scoped by grain type, so two different grain types can use the same key without referring to the same grain.

```csharp
public interface IDeviceGrain : IGrainWithStringKey
{
    ValueTask<string> GetStatus();
}

IDeviceGrain device = grainFactory.GetGrain<IDeviceGrain>("device-17");
```

Use a fixed key such as `"default"` when the application intentionally addresses one logical grain of a type. This is a convention, not a separate singleton feature.

## Read the current grain's key

Inside a grain, use the key helper matching its interface:

```csharp
public sealed class DeviceGrain : Grain, IDeviceGrain
{
    public ValueTask<string> GetStatus()
    {
        string deviceId = this.GetPrimaryKeyString();
        return ValueTask.FromResult($"Device {deviceId} is online");
    }
}
```

Other helpers include `GetPrimaryKey()`, `GetPrimaryKeyLong()`, and overloads that return a compound key's string extension.

The runtime identity is also available from `this.GetGrainId()` or `((IGrainBase)this).GrainContext.GrainId`. Use that form for infrastructure code that needs the grain type and encoded key together.

## Grain type names

By convention, Orleans derives a grain type name from the implementation class. Use <xref:Orleans.GrainTypeAttribute> when the type name must be stable independently of the CLR class name:

```csharp
[GrainType("shopping-cart")]
public sealed class ShoppingCartGrain : Grain, IShoppingCartGrain
{
}
```

Treat explicit grain type names as durable identifiers. Changing a deployed type name creates a different logical grain namespace and can disconnect existing references or persisted data from the new implementation.

## Work with GrainId

Advanced infrastructure can parse or create an untyped identity:

```csharp
GrainId grainId = GrainId.Create(
    GrainType.Create("shopping-cart"),
    IdSpan.Create("customer-42"));
```

Prefer `IGrainFactory.GetGrain<TGrainInterface>(key)` in application code. A typed reference carries both the logical identity and the interface used to call it, while a bare `GrainId` doesn't provide a callable contract.

## Why identity is logical

A grain identity isn't a process address. Orleans can activate the grain on any compatible silo, deactivate it when idle, and later recreate it elsewhere. References keep addressing the same logical grain throughout those changes. This location independence is what makes grain references safe to pass in calls and persist for later use.
