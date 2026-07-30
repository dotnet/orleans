---
title: Grain interface versioning
description: Learn how to use grain interface versioning in .NET Orleans.
ms.date: 05/23/2025
ms.topic: concept-article
---

# Grain interface versioning

In this article, you learn how to use grain interface versioning. The versioning of grain state is out of scope.

## Overview

On a given cluster, silos can support different versions of a grain type.

![Cluster with different versions of a grain](version.png)

In this example, the client and Silo{1,2,3} were compiled with grain interface `A` version 1. Silo 4 was compiled with `A` version 2.

## Limitations

- No versioning on stateless workers.
- Streaming interfaces aren't versioned.

## Enable versioning

If you don't explicitly add the version attribute to the grain interface, the grain has a default version of 0. You can version a grain by using the `VersionAttribute` on the grain interface:

```csharp
[Version(X)]
public interface IVersionUpgradeTestGrain : IGrainWithIntegerKey
{
}
```

Where `X` is the version number of the grain interface, which is typically monotonically increasing.

## Grain version compatibility and placement

When a call from a versioned grain arrives in a cluster:

- If no activation exists, Orleans creates a compatible activation.
- If an activation exists:
  - If the current activation isn't compatible, Orleans deactivates it and creates a new compatible one (see [Version selector strategy](version-selector-strategy.md)).
  - If the current activation is compatible (see [Compatible grains](compatible-grains.md)), Orleans handles the call normally.

By default:

- All versioned grains are assumed to be backward-compatible only (see [Backward compatibility guidelines](backward-compatibility-guidelines.md) and [Compatible grains](compatible-grains.md)). This means a v1 grain can make calls to a v2 grain, but a v2 grain cannot call a v1 grain.
- When multiple versions exist in the cluster, Orleans randomly places the new activation on a compatible silo.

You can change this default behavior via <xref:Orleans.Configuration.GrainVersioningOptions>:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleans(siloBuilder =>
{
    siloBuilder.Configure<GrainVersioningOptions>(options =>
    {
        options.DefaultCompatibilityStrategy = nameof(BackwardCompatible);
        options.DefaultVersionSelectorStrategy = nameof(MinimumVersion);
    });
});

using var host = builder.Build();
await host.RunAsync();
```
