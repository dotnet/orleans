---
title: Amazon DynamoDB grain persistence
description: Configure Amazon DynamoDB as an Orleans grain storage provider.
ms.date: 08/02/2026
ms.topic: how-to
---

# Amazon DynamoDB grain persistence

Install the `Microsoft.Orleans.Persistence.DynamoDB` package and configure a named provider with <xref:Orleans.Hosting.DynamoDBSiloBuilderExtensions.AddDynamoDBGrainStorage*>:

```csharp
siloBuilder.AddDynamoDBGrainStorage(
    "profileStore",
    options =>
    {
        options.Service = "us-west-2";
        options.ServiceId = "my-application";
        options.TableName = "OrleansGrainState";
        options.CreateIfNotExists = false;
    });
```

The AWS SDK credential chain supplies credentials when `AccessKey` and `SecretKey` aren't set. In production, prefer workload credentials such as an IAM role over long-lived keys. `ProfileName`, `AccessKey`, `SecretKey`, and `Token` are available when the deployment environment requires explicit SDK configuration.

## Capacity and lifecycle

`UseProvisionedThroughput` selects provisioned-capacity behavior. When enabled, configure `ReadCapacityUnits` and `WriteCapacityUnits`. `CreateIfNotExists` and `UpdateIfExists` allow provider-managed table changes, but infrastructure-managed provisioning is usually preferable in production.

`DeleteStateOnClear` controls whether clearing state deletes the item or resets it. `TimeToLive` advances the item's expiration on every write; enable it only when disappearing state is valid application behavior.

The `ServiceId` must remain stable across deployments that share the same logical application state. The provider uses optimistic concurrency and rejects stale writes.

## Serialization

Set `DynamoDBStorageOptions.GrainStorageSerializer` to customize the stored representation. Changing serializers doesn't rewrite existing items, so the replacement must read the previous representation or be accompanied by a migration.
