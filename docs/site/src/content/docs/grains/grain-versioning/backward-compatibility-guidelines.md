---
title: Backward compatibility guidelines
description: Learn the backward compatibility guidelines in .NET Orleans.
ms.date: 05/23/2025
ms.topic: concept-article
---

# Backward compatibility guidelines

Writing backward-compatible code can be hard and difficult to test. This article discusses guidelines for writing backward-compatible code in .NET Orleans. It covers the usage of <xref:Orleans.CodeGeneration.VersionAttribute> and <xref:System.ObsoleteAttribute>.

## Never change the signature of existing methods

Because of how the Orleans serializer works, you should never change the signature of existing methods.

The following example is correct:

```csharp
[Version(1)]
public interface IMyGrain : IGrainWithIntegerKey
{
    // First method
    Task MyMethod(int arg);
}
```

```csharp
[Version(2)]
public interface IMyGrain : IGrainWithIntegerKey
{
    // Method inherited from V1
    Task MyMethod(int arg);

    // New method added in V2
    Task MyNewMethod(int arg, obj o);
}
```

This example is incorrect:

```csharp
[Version(1)]
public interface IMyGrain : IGrainWithIntegerKey
{
    // First method
    Task MyMethod(int arg);
}
```

```csharp
[Version(2)]
public interface IMyGrain : IGrainWithIntegerKey
{
    // Method inherited from V1
    Task MyMethod(int arg, obj o);
}
```

> [!IMPORTANT]
> Do NOT make this change in your code, as it's an example of a bad practice that leads to very bad side effects.

This is an example of what can happen if you just rename the parameter names. Assume you have the following two interface versions deployed in the cluster:

```csharp
[Version(1)]
public interface IMyGrain : IGrainWithIntegerKey
{
    // return a - b
    Task<int> Subtract(int a, int b);
}
```

```csharp
[Version(2)]
public interface IMyGrain : IGrainWithIntegerKey
{
    // return b - a
    Task<int> Subtract(int b, int a);
}
```

These methods seem identical. However, if the client calls with V1, and a V2 activation handles the request:

```csharp
var grain = client.GetGrain<IMyGrain>(0);
var result = await grain.Subtract(5, 4); // Will return "-1" instead of expected "1"
```

This happens due to how the internal Orleans serializer works.

## Avoid changing existing method logic

It might seem obvious, but be very careful when changing the body of an existing method. Unless you're fixing a bug, it's better to add a new method if you need to modify the code.

Example:

```csharp
// V1
public interface MyGrain : IMyGrain
{
    // First method
    Task MyMethod(int arg)
    {
        SomeSubRoutine(arg);
    }
}
```

```csharp
// V2
public interface MyGrain : IMyGrain
{
    // Method inherited from V1
    // Do not change the body
    Task MyMethod(int arg)
    {
        SomeSubRoutine(arg);
    }

    // New method added in V2
    Task MyNewMethod(int arg)
    {
        SomeSubRoutine(arg);
        NewRoutineAdded(arg);
    }
}
```

## Do not remove methods from grain interfaces

Unless you're sure they're no longer used, don't remove methods from the grain interface. If you want to remove methods, do it in two steps:

1. Deploy V2 grains, with the V1 method marked as `Obsolete`.

    ```csharp
    [Version(1)]
    public interface IMyGrain : IGrainWithIntegerKey
    {
        // First method
        Task MyMethod(int arg);
    }
    ```

    ```csharp
    [Version(2)]
    public interface IMyGrain : IGrainWithIntegerKey
    {
        // Method inherited from V1
        [Obsolete]
        Task MyMethod(int arg);

        // New method added in V2
        Task MyNewMethod(int arg, obj o);
    }
    ```

1. When you're sure no V1 calls are being made (effectively, V1 is no longer deployed in the running cluster), deploy V3 with the V1 method removed.

    ```csharp
    [Version(3)]
    public interface IMyGrain : IGrainWithIntegerKey
    {
        // New method added in V2
        Task MyNewMethod(int arg, obj o);
    }
    ```
