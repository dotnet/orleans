---
title: Orleans grain directories
description: Choose and configure Orleans grain directories.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Orleans grain directories

A grain directory maps a grain identity to the silo that currently hosts its activation. Orleans consults the directory when routing calls and coordinating single-activation grain placement.

## Start with the default directory

Orleans uses the built-in `LocalGrainDirectory` by default. Despite its name, the directory is distributed across the cluster using a consistent-hash ring. It requires no external service and is the right starting point for most applications.

The default directory is eventually consistent during membership changes. A brief duplicate activation is possible during failures; Orleans resolves the conflict and deactivates the duplicate. Grain state and operations should therefore tolerate activation races and retries.

## Which grain directory should you use?

Use a pluggable directory for grain types that need different operational characteristics:

<a id="adonet-grain-directory"></a>
<a id="supported-databases"></a>
<a id="installation"></a>
<a id="adonetgraindirectoryoptions"></a>
<a id="database-setup"></a>

| Directory | Package | Consider it when |
|---|---|---|
| Redis | [`Microsoft.Orleans.GrainDirectory.Redis`](https://www.nuget.org/packages/Microsoft.Orleans.GrainDirectory.Redis) | A shared Redis service already meets latency and availability requirements. |
| Azure Table Storage | [`Microsoft.Orleans.GrainDirectory.AzureStorage`](https://www.nuget.org/packages/Microsoft.Orleans.GrainDirectory.AzureStorage) | Azure Table is the preferred shared backing service. |
| [Google Cloud Firestore](configuration-guide/google-firestore-providers.md) | [`Microsoft.Orleans.GrainDirectory.GoogleFirestore`](https://www.nuget.org/packages/Microsoft.Orleans.GrainDirectory.GoogleFirestore) | A shared Firestore database already meets latency and availability requirements. |
| ADO.NET | [`Microsoft.Orleans.GrainDirectory.AdoNet`](https://www.nuget.org/packages/Microsoft.Orleans.GrainDirectory.AdoNet) | Grain locations should use an existing supported relational database. |
| Custom | Application or third-party package | The application has a backend-specific requirement not met by built-in providers. |

External directories add network calls and another availability dependency. Apply them selectively and load-test activation-heavy workloads.

## Configuration

<a id="grain-configuration"></a>
<a id="silo-configuration"></a>

Register the provider under a name and select it on the grain implementation:

:::code language="csharp" source="snippets/hosting/HostingExamples.cs" id="named_grain_directory":::

:::code language="csharp" source="snippets/hosting/HostingExamples.cs" id="grain_directory_attribute":::

Grain types without <xref:Orleans.GrainDirectory.GrainDirectoryAttribute> continue to use the default directory. You can register multiple named providers for different grain types.

To replace the default for all unannotated grain types, use the provider's `Use...GrainDirectoryAsDefault` extension, for example <xref:Orleans.Hosting.RedisGrainDirectoryExtensions.UseRedisGrainDirectoryAsDefault*>, <xref:Orleans.Hosting.AzureTableGrainDirectorySiloBuilderExtensions.UseAzureTableGrainDirectoryAsDefault*>, or <xref:Orleans.Hosting.AdoNetGrainDirectorySiloBuilderExtensions.UseAdoNetGrainDirectoryAsDefault*>.

Named directories can also be configured under `Orleans:GrainDirectory:{name}` with an installed declarative provider:

```json
{
  "Orleans": {
    "GrainDirectory": {
      "durable-directory": {
        "ProviderType": "Redis",
        "ConnectionString": "redis.example.com:6380,ssl=true"
      }
    }
  }
}
```

## Strongly-consistent in-cluster directory

<a id="key-features"></a>
<a id="when-to-use"></a>

<xref:Orleans.Hosting.CoreHostingExtensions.AddDistributedGrainDirectory*> adds a strongly consistent in-cluster directory based on partitioned ranges and membership views.

> [!CAUTION]
> <xref:Orleans.Hosting.CoreHostingExtensions.AddDistributedGrainDirectory*> is experimental and emits diagnostic `ORLEANSEXP003`. Its API and behavior can change or be removed. It is not the default grain directory.

:::code language="csharp" source="snippets/hosting/HostingExamples.cs" id="distributed_grain_directory":::

The experimental directory defaults to one partition per silo (<xref:Orleans.Configuration.GrainDirectoryOptions.PartitionsPerSilo> = `1`). Change this only after testing with the expected cluster size and workload.

Evaluate it when stronger coordination during membership changes is worth adopting an experimental feature. Keep a rollout and rollback plan, and don't describe it as a drop-in production default.

## Operational guidance

- Keep directory backend latency low; activation and first-call latency depend on it.
- Provision external directories for the aggregate cluster workload and failure bursts.
- Don't use the grain directory as application state storage.
- Keep grain activation and deactivation idempotent.
- Test silo loss, rolling upgrades, and full-cluster restarts.
- Monitor duplicate-activation, directory, membership, and provider errors.

For architectural background, see [Grain directory implementation](../implementation/grain-directory.md).
