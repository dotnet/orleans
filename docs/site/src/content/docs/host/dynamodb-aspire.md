---
title: Use Amazon DynamoDB with Aspire
description: Configure Orleans clustering, grain storage, and reminders with AWS Aspire and DynamoDB.
ms.date: 08/25/2026
ms.topic: how-to
---

# Use Amazon DynamoDB with Aspire

Amazon DynamoDB can provide Orleans cluster membership, grain storage, and reminder storage in an Aspire application. Install `Aspire.Hosting.AWS` in the AppHost and the corresponding Orleans DynamoDB provider packages in the silo project:

- `Microsoft.Orleans.Clustering.DynamoDB`
- `Microsoft.Orleans.Persistence.DynamoDB`
- `Microsoft.Orleans.Reminders.DynamoDB`

## Provision the AWS topology with CDK

The AWS-supported `Aspire.Hosting.AWS` integration provisions DynamoDB tables through AWS CDK and supplies AWS SDK region and profile metadata to application resources. Define the Orleans resource identities and physical table schemas once, then use the same contract for CDK provisioning and Orleans provider configuration:

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="dynamodb_cdk_aspire":::

The shared topology declares the stable table names, partition keys, sort keys, and reminder indexes required by the Orleans providers:

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="dynamodb_cdk_topology":::

The provider configuration references the AWS SDK configuration and the table resource output. It emits the provider region independently so published workloads retain the required service location, and it gives grain storage the stable service ID. It sets `UseProvisionedThroughput`, `CreateIfNotExists`, and `UpdateIfExists` to `false` because CloudFormation owns table provisioning and updates:

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="dynamodb_provider_configuration":::

| Capability | Table key | Additional indexes |
|---|---|---|
| Cluster membership | `DeploymentId` + `SiloIdentity` | None |
| Grain storage | `GrainReference` + `GrainType` | None |
| Reminders | `ReminderId` + `GrainHash` | `ServiceIdIndex` and `ServiceIdGrainReferenceIndex` |
| Transactional state | `PartitionKey` + `RowKey` | None |
| Kinesis checkpoints | `CheckpointNamespace` + `Partition` | None |

The silo references all five tables so Aspire emits their stable names under `AWS:Resources:{resource-name}:TableName`. Clustering, grain storage, and reminders consume those outputs through `ServiceKey`. Transactional storage and Kinesis checkpoint configuration consume the corresponding outputs when those providers are enabled. Clients reference only the membership table. Silo and client resources wait for the CDK stack, and the client also waits for a silo.

Keep `ClusterId`, `ServiceId`, provider names, table names, and key schemas stable across deployments. Changing an identity or table name creates a separate data plane. Plan cutovers by provisioning the replacement topology, migrating or draining state where required, and then switching every silo and client to the same contract.

The AppHost credentials deploy and update a CloudFormation stack. Grant the provisioning identity the required CloudFormation, DynamoDB, and IAM permissions. Bootstrap the target account and region when the CDK application uses assets. Runtime workloads use the AWS SDK credential chain through task roles, pod identity, instance roles, or configured profiles; the generated provider environment contains region, profile, provider metadata, and table names rather than access keys or session tokens.

## Run with DynamoDB Local

Register a connection-string resource whose value is `Service=http://localhost:8000` (or another DynamoDB-compatible endpoint). The Orleans providers resolve it through `ServiceKey` and create their membership, grain-state, and reminder tables by default.

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="dynamodb_local_aspire":::

The explicit `IProviderConfiguration` maps the connection-string resource to the Orleans provider name. Orleans creates missing local tables so development and live provider tests exercise runtime recovery creation. The AppHost can start DynamoDB Local as a container separately and supply its endpoint through the connection string.

Use stable cluster and service identifiers whenever local data persists across AppHost runs. The three providers use separate default tables:

| Capability | Default table |
|---|---|
| Cluster membership | `OrleansSilos` |
| Grain storage | `OrleansGrainState` |
| Reminders | `OrleansReminders` |

## Use AWS profiles and workload credentials

`AddAWSSDKConfig().WithProfile(...).WithRegion(...)` supplies `AWS:Profile` and `AWS:Region` configuration together with the standard AWS environment variables. Orleans binds the profile and region while the AWS SDK resolves credentials through its normal credential chain. In deployed environments, workload credentials such as ECS task roles, EKS pod identity, and EC2 instance roles flow through that chain.

Direct provider configuration can set `AccessKey`, `SecretKey`, and `Token` for environments that supply explicit session credentials. `AccessKey`, `SecretKey`, and `Token` are redacted from formatted options. Configure `AccessKey` and `SecretKey` together, configure `Token` only with that pair, and choose either explicit credentials or `ProfileName`.

## Consume CloudFormation outputs

AWS Aspire CDK table references emit the table name under `AWS:Resources:{resource-name}:TableName`. A CloudFormation reference can instead target the provider section directly, such as `Orleans:Clustering`, `Orleans:GrainStorage:Default`, or `Orleans:Reminders`, when the stack exposes an output named `TableName`.

The providers also accept `ConnectionName`, `ConnectionString`, and nested `ConnectionProperties` or `Resource` values. Direct provider values take precedence over referenced resource outputs. A connection string can contain `Service`, `Region`, `ServiceURL`, `Endpoint`, `TableName`, `AccessKey`, `SecretKey`, `Token` or `SessionToken`, and `ProfileName` or `Profile`.

For infrastructure-managed tables, set `CreateIfNotExists` and `UpdateIfExists` to `false`. The compiled CDK example uses on-demand billing, so it also sets `UseProvisionedThroughput` to `false`.
