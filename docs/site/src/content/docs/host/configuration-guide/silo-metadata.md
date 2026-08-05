---
title: Silo metadata
description: Annotate Orleans silos for placement and application decisions.
ms.date: 08/02/2026
ms.topic: how-to
---

# Silo metadata

Silo metadata is an immutable string-to-string map published by each silo. Use it to describe placement-relevant capabilities such as region, hardware, role, or reservation type.

Metadata supports [grain placement filtering](../../grains/grain-placement-filtering.md). Don't put credentials, frequently changing health data, or large payloads in it.

## Configure metadata

`UseSiloMetadata()` reads `Orleans:Metadata`:

```json
{
  "Orleans": {
    "Metadata": {
      "cloud.region": "westus3",
      "hardware.accelerator": "gpu",
      "role": "recommendations"
    }
  }
}
```

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.UseSiloMetadata();
});
```

Environment variables can set deployment-specific values, for example:

```text
Orleans__Metadata__cloud.region=westus3
Orleans__Metadata__role=recommendations
```

You can also supply values programmatically:

```csharp
siloBuilder.UseSiloMetadata(new Dictionary<string, string>
{
    ["cloud.region"] = region,
    ["hardware.accelerator"] = hasGpu ? "gpu" : "none",
    ["role"] = "recommendations"
});
```

Metadata is fixed for the lifetime of a silo instance. Restart the silo to publish changed values.

## Read metadata

Inject `ISiloMetadataCache` into a silo service or Orleans component:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="read_silo_metadata":::

The cache follows cluster membership and fetches metadata from active silos. `GetSiloMetadata` returns the locally cached value, so it doesn't add a remote call to the request path.

## Define a metadata contract

- Use stable, namespaced keys such as `cloud.region` or `hardware.accelerator`.
- Treat key names and values as an application contract.
- Define behavior for missing or unknown values during rolling deployments.
- Keep values low-cardinality when they feed placement or telemetry.
- Ensure enough silos match every required placement filter.

Metadata complements heterogeneous silo configuration: use `GrainTypeOptions.Classes` when a silo cannot host a grain class, and metadata placement filters when it can host the class but should be selected based on capability.
