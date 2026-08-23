---
title: Use Amazon DynamoDB with Aspire
description: Configure Orleans clustering, grain storage, and reminders with AWS Aspire and DynamoDB.
ms.date: 08/23/2026
ms.topic: how-to
---

# Use Amazon DynamoDB with Aspire

Amazon DynamoDB can provide Orleans cluster membership, grain storage, and reminder storage in an Aspire application. Install `Aspire.Hosting.AWS` in the AppHost and the corresponding Orleans DynamoDB provider packages in the silo project:

- `Microsoft.Orleans.Clustering.DynamoDB`
- `Microsoft.Orleans.Persistence.DynamoDB`
- `Microsoft.Orleans.Reminders.DynamoDB`

## Run with DynamoDB Local

The AWS Aspire integration's `AddAWSDynamoDBLocal` resource supplies `AWS_ENDPOINT_URL_DYNAMODB` to referenced projects. The Orleans DynamoDB providers consume that endpoint and create their membership, grain-state, and reminder tables by default.

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="dynamodb_local_aspire":::

The explicit `IProviderConfiguration` maps the structured AWS resource to the Orleans provider name. `Aspire.Hosting.Orleans` currently accepts connection-string resources in its resource-based provider overloads, while the AWS DynamoDB Local and CDK resources expose endpoints and structured outputs. The configuration object keeps the provider selection in the AppHost model and the `WithReference(dynamodb)` call supplies the local endpoint.

Use stable cluster and service identifiers whenever local data persists across AppHost runs. The three providers use separate default tables:

| Capability | Default table |
|---|---|
| Cluster membership | `OrleansSilos` |
| Grain storage | `OrleansGrainState` |
| Reminders | `OrleansReminders` |

## Use AWS profiles and workload credentials

`AddAWSSDKConfig().WithProfile(...).WithRegion(...)` supplies `AWS:Profile` and `AWS:Region` configuration together with the standard AWS environment variables. Orleans binds the profile and region while the AWS SDK resolves credentials through its normal credential chain. In deployed environments, workload credentials such as ECS task roles, EKS pod identity, and EC2 instance roles flow through that chain.

Direct provider configuration can set `AccessKey`, `SecretKey`, and `Token` for environments that supply explicit session credentials. `AccessKey`, `SecretKey`, and `Token` are redacted from formatted options. Configure `AccessKey` and `SecretKey` together, configure `Token` only with that pair, and choose either explicit credentials or `ProfileName`.

## Consume CDK and CloudFormation outputs

AWS Aspire CDK table references emit the table name under `AWS:Resources:{resource-name}:TableName`. Set the Orleans provider's `ServiceKey` to the resource name, and the provider binds that structured table output. A CloudFormation reference can instead target the provider section directly, such as `Orleans:Clustering`, `Orleans:GrainStorage:Default`, or `Orleans:Reminders`, when the stack exposes an output named `TableName`.

The providers also accept `ConnectionName`, `ConnectionString`, and nested `ConnectionProperties` or `Resource` values. Direct provider values take precedence over referenced resource outputs. A connection string can contain `Service`, `Region`, `ServiceURL`, `Endpoint`, `TableName`, `AccessKey`, `SecretKey`, `Token` or `SessionToken`, and `ProfileName` or `Profile`.

For infrastructure-managed tables, set `CreateIfNotExists` and `UpdateIfExists` to `false`. Provision each table with the key schema and secondary indexes required by its Orleans capability before the silos start.
