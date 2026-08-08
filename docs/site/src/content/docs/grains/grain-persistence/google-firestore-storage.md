---
title: Google Cloud Firestore grain persistence
description: Configure Google Cloud Firestore as an Orleans grain storage provider.
ms.date: 08/07/2026
ms.topic: how-to
---

# Google Cloud Firestore grain persistence

Install the [`Microsoft.Orleans.Persistence.GoogleFirestore`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.GoogleFirestore) package and configure a named provider with <xref:Orleans.Hosting.FirestoreStorageHostingExtensions.AddGoogleFirestoreGrainStorage*>:

:::code language="csharp" source="../../host/snippets/google-firestore/GoogleFirestoreConfiguration.cs" id="google_firestore_persistence":::

The `ProjectId` identifies the Google Cloud project, `RootCollectionName` selects the provider's top-level collection, and `EmulatorHost` redirects requests to a local emulator. For database creation, authentication, IAM, clustering, reminders, and emulator setup, see [Google Cloud Firestore providers](../../host/configuration-guide/google-firestore-providers.md).

## Clear behavior

<xref:Orleans.Persistence.GoogleFirestore.FirestoreStateStorageOptions.DeleteStateOnClear> controls how <xref:Orleans.Core.IStorage.ClearStateAsync*> handles a record:

- `false`, the default, keeps an empty document and advances its ETag.
- `true` deletes the document.

Both modes use optimistic concurrency. A stale write or clear fails with <xref:Orleans.Storage.InconsistentStateException> instead of overwriting a newer record.

## Serialization and record size

The provider serializes each state record into one Firestore document. The serialized payload, document name, and metadata must fit within the [Firestore document-size limit](https://cloud.google.com/firestore/quotas). Test production-shaped state after serialization rather than relying only on in-memory object size.

Set <xref:Orleans.Persistence.GoogleFirestore.FirestoreStateStorageOptions.GrainStorageSerializer> to customize the stored representation. Changing serializers doesn't rewrite existing documents, so the replacement must read the previous representation or be accompanied by a migration.

## Sample

The [Google Cloud Firestore sample](https://github.com/dotnet/orleans/tree/main/samples/GoogleFirestore) configures persistence together with Firestore clustering, reminders, and the grain directory. Run it once to write state and run it again to observe the persisted counter.
