---
title: Orleans migration guides
description: Plan and execute an upgrade to Orleans 10 from Orleans 9, 8, 7, or an older release.
ms.date: 08/07/2026
ms.topic: how-to
ms.custom: migration-guide
---

# Orleans migration guides

Use the guide that matches the Orleans major version currently deployed in production.

| Starting version | Recommended path | Guide |
|------------------|------------------|-------|
| Orleans 9.x | Update to the latest 9.x patch, then upgrade to Orleans 10.x | [Upgrade Orleans 9.x to 10.x](migration/9-to-10.md) |
| Orleans 8.x | Update to Orleans 8.2, validate Orleans 9.2, then upgrade to Orleans 10.x | [Upgrade Orleans 8.x to 10.x](migration/8-to-10.md) |
| Orleans 7.x | Update to the latest 7.2 patch, then validate each supported major-version checkpoint | [Upgrade Orleans 7.x to 10.x](migration/7-to-10.md) |
| Orleans 3.x or earlier | Migrate to Orleans 7 in a separate cluster before continuing sequentially | [Archived Orleans 3.x to 7.x notes](migration/3-to-7-archive.md) |

> [!IMPORTANT]
> Orleans documents mixed-version deployment safety only within one major-version family. Don't put Orleans 9 and Orleans 10 silos in the same production cluster without completing your own compatibility qualification. The guides use a parallel-cluster deployment as the safe default for crossing major versions.

## What every upgrade must preserve

Before changing packages, record the following compatibility contract:

- The Orleans, .NET, provider, and database versions currently deployed.
- The grain interface and serialized type assemblies used by every silo and client.
- Stable serializer member IDs and type aliases for data in grain storage, streams, reminders, and queued messages.
- The clustering, persistence, reminder, stream, and grain-directory providers and their schema versions.
- Explicit values for behavior-sensitive options, including request cancellation, placement, grain directory caching, and timer interleaving.
- A backup or recovery point for durable state and provider metadata.

See [Upgrade deployment and rollback](migration/deployment-and-rollback.md) before choosing a deployment strategy.

## POCO grains and <xref:Orleans.IGrainBase> <a name="poco-grains-and-igrainbase"></a>

POCO grains remain supported. A grain that doesn't inherit from <xref:Orleans.Grain> implements <xref:Orleans.IGrainBase> and receives its <xref:Orleans.Runtime.IGrainContext> through dependency injection. This also enables grain extension methods such as timers, reminders, streaming, deactivation, and migration.

## Package version policy

Keep all `Microsoft.Orleans.*` packages on the same 10.x patch. For solutions that use NuGet Central Package Management, declare the versions in `Directory.Packages.props` and omit versions from project-level `PackageReference` items. Don't copy old provider dependency versions from migration examples; select a current version that is supported by your target runtime and provider.

For related guidance, see:

- [Orleans serialization](host/configuration-guide/serialization.md)
- [Deploy new versions of grains](grains/grain-versioning/deploying-new-versions-of-grains.md)
- [ADO.NET provider configuration](host/configuration-guide/configuring-ado-dot-net-providers.md)
- [Deployment troubleshooting](deployment/troubleshooting-deployments.md)
