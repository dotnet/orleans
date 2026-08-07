---
title: Amazon DynamoDB grain persistence
description: Configure Amazon DynamoDB as an Orleans grain storage provider.
ms.date: 08/02/2026
ms.topic: how-to
---

# Amazon DynamoDB grain persistence

Install the [`Microsoft.Orleans.Persistence.DynamoDB`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.DynamoDB) package and configure a named provider with <xref:Orleans.Hosting.DynamoDBGrainStorageSiloBuilderExtensions.AddDynamoDBGrainStorage*>:

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

The [AWS SDK for .NET credential and profile resolution chain](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/creds-assign.html) supplies credentials when <xref:Orleans.Persistence.DynamoDB.DynamoDBClientOptions.AccessKey> and <xref:Orleans.Persistence.DynamoDB.DynamoDBClientOptions.SecretKey> aren't set. In production, prefer workload credentials such as an IAM role over long-lived keys. <xref:Orleans.Persistence.DynamoDB.DynamoDBClientOptions.ProfileName>, <xref:Orleans.Persistence.DynamoDB.DynamoDBClientOptions.AccessKey>, <xref:Orleans.Persistence.DynamoDB.DynamoDBClientOptions.SecretKey>, and <xref:Orleans.Persistence.DynamoDB.DynamoDBClientOptions.Token> are available when the deployment environment requires explicit SDK configuration. See the [AWS CLI configuration-file reference](https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-files.html) for profile configuration.

## Capacity and lifecycle

<xref:Orleans.Configuration.DynamoDBStorageOptions.UseProvisionedThroughput> selects provisioned-capacity behavior. When enabled, configure <xref:Orleans.Configuration.DynamoDBStorageOptions.ReadCapacityUnits> and <xref:Orleans.Configuration.DynamoDBStorageOptions.WriteCapacityUnits>. <xref:Orleans.Configuration.DynamoDBStorageOptions.CreateIfNotExists> and <xref:Orleans.Configuration.DynamoDBStorageOptions.UpdateIfExists> allow provider-managed table changes, but infrastructure-managed provisioning is usually preferable in production.

<xref:Orleans.Configuration.DynamoDBStorageOptions.DeleteStateOnClear> controls whether clearing state deletes the item or resets it. <xref:Orleans.Configuration.DynamoDBStorageOptions.TimeToLive> advances the item's expiration on every write; enable it only when disappearing state is valid application behavior.

<xref:Orleans.Configuration.DynamoDBStorageOptions.ServiceId> must remain stable across deployments that share the same logical application state. The provider uses optimistic concurrency and rejects stale writes.

## Serialization

Set <xref:Orleans.Configuration.DynamoDBStorageOptions.GrainStorageSerializer> to customize the stored representation. Changing serializers doesn't rewrite existing items, so the replacement must read the previous representation or be accompanied by a migration.
