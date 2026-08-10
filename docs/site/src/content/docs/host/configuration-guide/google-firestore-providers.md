---
title: Google Cloud Firestore providers
description: Configure Google Cloud Firestore for Orleans clustering, grain directories, reminders, and persistence.
ms.date: 08/07/2026
ms.topic: how-to
---

# Google Cloud Firestore providers

Orleans provides the following Google Cloud Firestore integrations:

| Capability | Package |
|---|---|
| Cluster membership and client gateway discovery | [`Microsoft.Orleans.Clustering.Firestore`](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.Firestore) |
| Grain directory | [`Microsoft.Orleans.GrainDirectory.Firestore`](https://www.nuget.org/packages/Microsoft.Orleans.GrainDirectory.Firestore) |
| Grain persistence | [`Microsoft.Orleans.Persistence.Firestore`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.Firestore) |
| Reminders | [`Microsoft.Orleans.Reminders.Firestore`](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.Firestore) |

Create the [`(default)` Firestore database in Native mode](https://cloud.google.com/firestore/docs/create-database-server-client-library) before starting the cluster. The providers use the Google Cloud .NET client library and therefore use [Application Default Credentials (ADC)](https://cloud.google.com/docs/authentication/provide-credentials-adc) when connecting to Google Cloud. Prefer workload identity or an attached service account over long-lived service-account keys.

Grant the application's identity only the [Firestore IAM permissions](https://cloud.google.com/firestore/native/docs/security/iam) needed to create, read, update, query, and delete provider documents.

## Configure a silo

Configure every installed Firestore provider with the same Google Cloud project and root collection. The following example configures all four providers:

:::code language="csharp" source="../snippets/google-firestore/FirestoreConfiguration.cs" id="google_firestore_silo":::

The configuration uses:

- <xref:Orleans.Configuration.ClusterOptions.ClusterId> to partition cluster membership and grain-directory records.
- <xref:Orleans.Configuration.ClusterOptions.ServiceId> to partition reminders and persistent grain state.
- `RootCollectionName` as the top-level Firestore collection. Its default value is `Orleans`.

Keep `ClusterId` and `ServiceId` stable for the lifetime of a deployment. Use different values when deployments must not share membership, grain locations, reminders, or state.

The clustering provider reads membership rows and the table version in a [serializable Firestore transaction](https://cloud.google.com/firestore/docs/transaction-data-contention#serializable_isolation). Topology-changing membership inserts and updates atomically write the changed silo row and monotonically advance the version row, so a read can't combine rows from one topology version with the version from another.

<xref:Orleans.Hosting.FirestoreGrainDirectoryExtensions.UseFirestoreGrainDirectoryAsDefault*> replaces the built-in directory for every grain type which doesn't explicitly select another directory. External directories add a Firestore request to directory operations, so benchmark activation-heavy workloads before using one as the default.

For details about state records, serializers, and clear behavior, see [Google Cloud Firestore grain persistence](../../grains/grain-persistence/google-firestore-storage.md).

## Configure an external client

External clients use the clustering package to discover active Orleans gateways:

:::code language="csharp" source="../snippets/google-firestore/FirestoreConfiguration.cs" id="google_firestore_client":::

The client's `ClusterId`, `ServiceId`, `ProjectId`, and `RootCollectionName` must match the silo configuration.

## Use the emulator

Set `EmulatorHost` to the Firestore emulator endpoint, such as `127.0.0.1:8080`. Emulator connections use an insecure local channel and don't use Google Cloud credentials. Don't set `EmulatorHost` in production.

The [Firebase Local Emulator Suite](https://firebase.google.com/docs/emulator-suite/connect_firestore) can run Firestore locally. The [Google Cloud Firestore sample](https://github.com/dotnet/orleans/tree/main/samples/GoogleFirestore) includes commands for starting the emulator and exercising all four providers.

## Operational guidance

- Locate silos near the Firestore database and account for Firestore latency on membership, reminder, directory, and persistence operations.
- Monitor request errors, latency, quotas, and billing. Review the [Firestore quotas and limits](https://cloud.google.com/firestore/quotas) against the expected cluster and activation rate.
- Back up persistent state according to the application's recovery requirements. Membership, gateway, directory, and reminder records are operational data and should be isolated from application-owned collections.
- Restrict direct writes to provider-owned documents. Mutating them outside Orleans can violate membership, reminder, directory, or optimistic-concurrency invariants.
- Test rolling upgrades, silo loss, credential rotation, quota exhaustion, and Firestore unavailability before production deployment.
