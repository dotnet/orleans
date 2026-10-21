---
title: Orleans grain directories
description: Choose and configure Orleans grain directories.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Orleans grain directories

A grain directory maps a grain identity to the silo that currently hosts its activation. Orleans consults the directory when routing calls and coordinating single-activation grain placement.

## Start with the default directory

Orleans uses the built-in `DistributedGrainDirectory` by default. It keeps registrations in the cluster and coordinates partition ownership using versioned membership views. It requires no external service.

The directory coordinates registration and ownership transfer so that callers agree on an activation's location as membership changes. Keep grain operations idempotent: failed calls and retries can still repeat application work.

Applications retaining the previous `LocalGrainDirectory` select it with <xref:Orleans.Hosting.CoreHostingExtensions.UseLocalGrainDirectory*> on every silo before migrating. See [Upgrade from the local directory](#local-to-distributed-directory-upgrade) for the one-way migration procedure.

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
| [Google Cloud Firestore](configuration-guide/google-firestore-providers.md) | [`Microsoft.Orleans.GrainDirectory.Firestore`](https://www.nuget.org/packages/Microsoft.Orleans.GrainDirectory.Firestore) | A shared Firestore database already meets latency and availability requirements. |
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

The default `DistributedGrainDirectory` is a strongly consistent in-cluster directory based on partitioned ranges and membership views. <xref:Orleans.Hosting.CoreHostingExtensions.AddDistributedGrainDirectory*> selects it explicitly or registers an additional name for the same directory instance.

Keep the final default and named directory mappings consistent across the cluster. Multiple names share the same distributed directory instance. Use the upgrade procedure below to transition an existing local-directory cluster.

The distributed directory defaults to one partition per silo (<xref:Orleans.Configuration.GrainDirectoryOptions.PartitionsPerSilo> = `1`). Change this only after testing with the expected cluster size and workload.

<xref:Orleans.Configuration.GrainDirectoryOptions.GetPartitionBoundaries> configures the function which assigns starting hash boundaries to numbered partitions. The default uses the silo's consistent hash for a single partition and cached uniform hashes in generation order for multiple partitions. A custom function returns an initialized <xref:System.Collections.Immutable.ImmutableArray`1> containing exactly the configured number of boundaries, with each index identifying a partition. Keep each partition's starting boundary stable throughout the silo's membership and use equivalent functions on every silo. The immutable array shares its backing storage safely with directory membership snapshots.

If the boundary function throws or returns an invalid result, Orleans stops directory membership processing and reports an <xref:Orleans.Runtime.OrleansConfigurationException> to membership readers and refresh calls. Correct the boundary configuration and restart the affected silos.

The partition count and boundary function are captured when the directory is created. Keep both consistent across the cluster, including during rollout and rollback. See [Distributed-directory upgrade compatibility](../deployment/upgrades.md#distributed-directory-upgrade-compatibility) for retaining the mapping used by an existing cluster.

## Upgrade from the local directory

<a id="local-to-distributed-directory-upgrade"></a>

The distributed directory is stable and enabled by default. Clusters upgrading from a runtime which selected `LocalGrainDirectory` by default should separate the runtime upgrade from the directory migration. See [Distributed-directory upgrade compatibility](../deployment/upgrades.md#distributed-directory-upgrade-compatibility) for release-specific prerequisites.

**Directory migration is one-way.** Once a distributed-directory silo joins the migration, continue toward an all-distributed cluster. Returning that cluster to the local directory is unsupported. Decide the recovery procedure and qualify the deployment before introducing the first distributed-directory silo.

### Prepare and defer migration

1. Inventory the default and named directories selected by every grain type. Keep named-provider selections and cluster identity unchanged during this procedure.
1. Qualify the exact application versions, serialized contracts, persistence providers, and deployment configuration under representative traffic. Verify duplicate-activation detection, registration preservation, graceful silo departure, and application recovery.
1. Persist state needed after reactivation and budget capacity for activation recovery. Legacy directory-owner departures can deactivate affected grains on surviving silos; later calls recreate them through the current directory owner.
1. Roll out the new runtime with <xref:Orleans.Hosting.CoreHostingExtensions.UseLocalGrainDirectory*> configured on **every replacement silo**. This explicitly retains the local directory while the runtime versions coexist. Keep that configuration on scale-out instances too.
1. Wait until every silo runs the new runtime, membership is stable, and grain-call and activation behavior match the baseline. Applications can remain in this all-local configuration until ready for the directory migration.

`UseLocalGrainDirectory` is a migration-deferral option. Choose it before the directory migration starts. Its API reference provides the configuration surface; the released package used by the hosting snippets predates this API.

### Migrate the directory

1. Keep <xref:Orleans.Configuration.GrainDirectoryOptions.PartitionsPerSilo> at `1` and retain the default <xref:Orleans.Configuration.GrainDirectoryOptions.GetPartitionBoundaries> function throughout the local-to-distributed rollout.
1. Remove `UseLocalGrainDirectory` from the deployment configuration. The runtime default selects the distributed directory; an explicit call to <xref:Orleans.Hosting.CoreHostingExtensions.AddDistributedGrainDirectory*> also selects it.
1. Replace a bounded number of silos at a time, preserving the tested ready-silo and surge-capacity floor. Gracefully stop each departing silo, and allow membership and directory ownership transfers to complete before the next replacement.
1. During each mixed-directory stage, exercise existing grains and newly activated grains. Verify activation uniqueness and that active grains retain their directory registrations, including after a local-directory silo leaves.
1. Continue until every silo uses the distributed directory. Keep the distributed selection and partition configuration on later deployments and scale-out instances.

The runtime compatibility endpoints let local- and distributed-directory silos route registrations and transfer ownership during this transition. The staged runtime upgrade ensures every participant has the migration protocol used by the new release.

### Watch the migration

Compare grain-call error rate and latency, activation churn and failures, duplicate-activation reports, membership churn, and directory registration errors with the pre-upgrade baseline. Observe directory snapshot transfers, range recovery, and range-lock duration alongside silo departures. A healthy checkpoint has a stable membership view, completed transfers, successful calls to existing and new grains, and one registered activation per single-activation grain.

Pause replacements when a safety or availability threshold is exceeded. Preserve directory and membership logs, retain sufficient distributed-directory capacity, and investigate the failed transition before continuing. Recover application state using the qualified recovery procedure while retaining the distributed directory.

### Clusters already using the experimental distributed directory

Keep their established partition count and boundary-to-partition mapping throughout the runtime upgrade. Experimental releases used `30` partitions per silo with uniform hashes sorted by unsigned value. Current releases use one partition by default and use hash-generation order when multiple partitions are configured. Configure the established mapping explicitly before newer silos join; see [Distributed-directory upgrade compatibility](../deployment/upgrades.md#distributed-directory-upgrade-compatibility) for the release boundaries and configuration.

## Operational guidance

- Keep directory backend latency low; activation and first-call latency depend on it.
- Provision external directories for the aggregate cluster workload and failure bursts.
- Don't use the grain directory as application state storage.
- Keep grain activation and deactivation idempotent.
- Test silo loss, rolling upgrades, and full-cluster restarts.
- Monitor duplicate-activation, directory, membership, and provider errors.

For architectural background, see [Grain directory implementation](../implementation/grain-directory.md).
