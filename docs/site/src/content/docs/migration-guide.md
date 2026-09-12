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

## Activation metric schema update

**Release note for the next release:** Activation lifecycle counters, latency histograms, and population gauges now include `grain_type`, using the canonical `GrainId.Type.ToString()` identity. `orleans-grains` migrates from the `type` key containing a CLR implementation name to `grain_type` containing that canonical identity. Explicitly named grains and constructed generic grains can therefore have different values as well as a different key.

Update dashboard groupings, filters, recording rules, and OpenTelemetry views to retain `grain_type`. For a mixed-version rollout, use the emitting service version to select the old or new schema. Map old CLR names to canonical names using the deployment's grain-type registrations before combining series. Count each emitter's series once in combined views.

Activation gauges now emit a snapshot for each cached type, including zero after the last activation leaves. Obtain cluster totals by summing the latest fresh observation from each silo and type. Preserve emitter identity through export and configure stale-series expiry in the backend. Lifecycle counters retain additive event semantics: summing their per-type increases over the same interval recovers total event volume.

The shutdown counter retains its historical additional event for each nonempty collection batch with `grain_type=unknown`, `via=collection`; individual shutdown events carry their activation's canonical type. The collection-scan counter retains scan-level measurements. `orleans-system-targets` retains its `type` key, and management grain statistics retain CLR display names with per-silo accounting.

See the [metrics catalog](host/monitoring/metrics-catalog.md#grain-type-identity-and-aggregation) for units, lifecycle outcomes, population scopes, and the `unknown` convention.

## Package version policy

Keep all `Microsoft.Orleans.*` packages on the same 10.x patch. For solutions that use NuGet Central Package Management, declare the versions in `Directory.Packages.props` and omit versions from project-level `PackageReference` items. Don't copy old provider dependency versions from migration examples; select a current version that is supported by your target runtime and provider.

For related guidance, see:

- [Orleans serialization](host/configuration-guide/serialization.md)
- [Deploy new versions of grains](grains/grain-versioning/deploying-new-versions-of-grains.md)
- [ADO.NET provider configuration](host/configuration-guide/configuring-ado-dot-net-providers.md)
- [Deployment troubleshooting](deployment/troubleshooting-deployments.md)
