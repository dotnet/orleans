---
title: Silo metadata
description: Learn about silo metadata in .NET Orleans.
ms.date: 01/21/2026
---

# Silo metadata

Silo metadata is a feature in Orleans that allows developers to assign custom metadata to silos within a cluster. This metadata provides a flexible mechanism for annotating silos with descriptive information or specific capabilities.

This feature is particularly useful in scenarios where different silos have distinct roles, hardware configurations, or other unique characteristics. For example, silos can be tagged based on their region, compute power, or specialized responsibilities within the system.

Silo metadata lays the groundwork for additional Orleans features, such as [Grain placement filtering](../../grains/grain-placement-filtering.md).

## Key concepts

Silo metadata introduces a way to attach key-value pairs to silos within an Orleans cluster. This feature allows developers to configure silo-specific characteristics that can be leveraged by Orleans components.

Silo metadata is represented as an **immutable** dictionary of key-value pairs:

- **Keys**: Strings that identify the metadata (for example, `"cloud.region"`, `"compute.reservation.type"`).
- **Values**: Strings that describe the corresponding property (for example, `"us-east1"`, `"spot"`).

## Configuration

Silo metadata in Orleans is configured using two methods, either .NET configuration or directly in code.

### Configure Silo metadata with configuration

Silo metadata can be defined in the app's configuration, such as _appsettings.json_, environment variables, or any other available configuration source.

#### Example: _appsettings.json_ configuration

```json
{
  "Orleans": {
    "Metadata": {
      "cloud.region": "us-east1",
      "compute.reservation.type": "spot",
      "role": "worker"
    }
  }
}
```

The preceding configuration defines metadata for a Silo, tagging it with:

- `cloud.region`: `"us-east1"`
- `compute.reservation.type`: `"spot"`
- `role`: `"worker"`

To apply this configuration, use the following setup in your silo host builder:

```csharp
Host.CreateApplicationBuilder(args).UseOrleans(siloBuilder =>
{
    // Configuration section Orleans:Metadata is used by default
    siloBuilder.UseSiloMetadata();
});
```

Alternatively, an explicit `IConfiguration` or `IConfigurationSection` can be passed in to control where in configuration the metadata is pulled from.

### Configuring silo metadata in code

For scenarios requiring programmatic metadata configuration, developers can add metadata directly in the Silo host builder.

#### Example: Direct Code Configuration

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleans(siloBuilder =>
{
    siloBuilder.UseSiloMetadata(new Dictionary<string, string>
    {
        ["cloud.region"] = "us-east1",
        ["compute.reservation.type"] = "spot",
        ["role"] = "worker"
    });
});
```

The preceding example achieves the same result as the JSON configuration but allows metadata values to be computed or loaded dynamically during silo initialization.

### Merge configurations

If both .NET configuration and direct code configuration are used, the direct configuration overrides any conflicting metadata values from the .NET configuration. This allows developers to set defaults with configuration files and dynamically adjust specific metadata during runtime.

## Usage

Developers can retrieve metadata through the `ISiloMetadataCache` interface. This interface allows for querying metadata for individual silos across the cluster. Metadata will always be returned from a local cache of metadata that gets updated in the background as cluster membership changes.

### Access metadata for a specific silo

The `ISiloMetadataCache` provides a method to retrieve the metadata for a specific silo by its unique identifier (<xref:Orleans.Runtime.SiloAddress>). The `ISoloMetadataCache` implementation is registered in the `UseSiloMetadata` method and can be injected as a dependency.

#### Example: Access metadata for a Silo

```csharp
var siloMetadata = siloMetadataCache.GetSiloMetadata(siloAddress);

if (siloMetadata.Metadata.TryGetValue("role", out var role))
{
    Console.WriteLine($"Silo Role for {siloAddress}: {role}");
    // Execute role-specific logic
}
```

In the preceding example:

- `GetSiloMetadata(siloAddress)` retrieves the metadata for the specified silo.
- Metadata keys like `"role"` can be used to influence application logic.

## Internal implementation

Internally, the `SiloMetadataCache` monitors changes in cluster membership on `MembershipTableManager` and keeps a local cache of metadata in sync with membership changes. Metadata is immutable for a given Silo, so it's retrieved once and cached until that Silo leaves the cluster. Cached metadata for clusters that are `Dead` or have left the membership table will be cleared out of the local cache.

Each silo hosts an `ISystemTarget` that provides that silo's metadata. Calls to `SiloMetadataCache : ISiloMetadataCache` return a result from the local cache.
