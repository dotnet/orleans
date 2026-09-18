---
title: Configure Amazon DynamoDB reminders
description: Configure durable Orleans reminder storage with Amazon DynamoDB.
ms.date: 08/20/2026
ms.topic: how-to
---

# Configure Amazon DynamoDB reminders

Install the [`Microsoft.Orleans.Reminders.DynamoDB`](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.DynamoDB) package and call <xref:Orleans.Hosting.DynamoDBSiloBuilderReminderExtensions.UseDynamoDBReminderService*> on every silo.

A silo which uses DynamoDB for both membership and reminders configures the providers independently:

:::code language="csharp" source="../../snippets/compiled/Grains/DynamoDBReminderSnippets.cs" id="configure_dynamodb_reminders":::

<xref:Orleans.Configuration.DynamoDBClusteringOptions> and <xref:Orleans.Configuration.DynamoDBReminderStorageOptions> are separate typed options. Set the AWS region through each options instance, as shown above. A silo which uses another membership provider configures that provider alongside <xref:Orleans.Hosting.DynamoDBSiloBuilderReminderExtensions.UseDynamoDBReminderService*>.

<xref:Orleans.Configuration.ClusterOptions.ServiceId> identifies the application's reminder records and must remain stable across deployments that share a reminder table. Use distinct table names for cluster membership and reminders because each provider manages a different schema.

## Configure AWS credentials

When <xref:Orleans.Reminders.DynamoDB.DynamoDBClientOptions.AccessKey> and <xref:Orleans.Reminders.DynamoDB.DynamoDBClientOptions.SecretKey> remain unset, the [AWS SDK for .NET credential and profile resolution chain](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/creds-assign.html) supplies credentials. In production, prefer workload credentials such as an IAM role over long-lived keys. <xref:Orleans.Reminders.DynamoDB.DynamoDBClientOptions.ProfileName>, <xref:Orleans.Reminders.DynamoDB.DynamoDBClientOptions.AccessKey>, <xref:Orleans.Reminders.DynamoDB.DynamoDBClientOptions.SecretKey>, and <xref:Orleans.Reminders.DynamoDB.DynamoDBClientOptions.Token> support deployment environments which require explicit SDK configuration.

## Configure table capacity and lifecycle

The example uses on-demand capacity and an infrastructure-managed table. Set <xref:Orleans.Configuration.DynamoDBReminderStorageOptions.UseProvisionedThroughput> to `true` and configure <xref:Orleans.Configuration.DynamoDBReminderStorageOptions.ReadCapacityUnits> and <xref:Orleans.Configuration.DynamoDBReminderStorageOptions.WriteCapacityUnits> for provisioned capacity.

<xref:Orleans.Configuration.DynamoDBReminderStorageOptions.CreateIfNotExists> and <xref:Orleans.Configuration.DynamoDBReminderStorageOptions.UpdateIfExists> allow the provider to create the reminder table and update its provisioned capacity. Infrastructure-managed provisioning keeps table lifecycle and capacity changes in the deployment workflow.

For Aspire AppHost configuration, DynamoDB Local, and structured AWS CDK or CloudFormation outputs, see [Use Amazon DynamoDB with Aspire](../../host/dynamodb-aspire).
