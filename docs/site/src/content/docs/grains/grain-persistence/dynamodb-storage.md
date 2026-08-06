---
title: Amazon DynamoDB grain persistence
description: Learn about Amazon DynamoDB grain persistence in .NET Orleans.
ms.date: 02/06/2026
ms.topic: how-to
ai-usage: ai-assisted
zone_pivot_groups: orleans-version
---

# Amazon DynamoDB grain persistence

In this article, you learn how to install and configure Amazon DynamoDB grain persistence.

## Installation

Install the [`Microsoft.Orleans.Persistence.DynamoDB`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.DynamoDB) package from NuGet.

## Configuration

Configure the DynamoDB grain persistence provider using the <xref:Orleans.Hosting.DynamoDBSiloBuilderExtensions.AddDynamoDBGrainStorage*?displayProperty=nameWithType> extension method.

```csharp
siloBuilder.AddDynamoDBGrainStorage(
    name: "profileStore",
    configureOptions: options =>
    {
        options.AccessKey = "<DynamoDB access key>";
        options.SecretKey = "<DynamoDB secret key>";
        options.Service = "<DynamoDB region name>"; // Such as "us-west-2"
    });
);
```

If your authentication method requires a token or a non-default profile name, you can define those properties. First, view your credentials file using the following command:

```bash
cat ~/.aws/credentials
```

As an example, the following configuration shows how to configure the DynamoDB grain persistence provider to use the `default` profile from the `~/.aws/credentials` file:

```bash
[YOUR_PROFILE_NAME]
aws_access_key_id = ***
aws_secret_access_key = ***
aws_security_token = ***
aws_session_expiration = ***
aws_session_token = ***
```

This configuration allows for both types of authentication credentials:

- access key & secret key
- access key & secret key & token

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

``` csharp
siloBuilder.AddDynamoDBGrainStorage(
  name: "profileStore",
  configureOptions: options =>
  {
      options.AccessKey = "***";
      options.SecretKey = "***";
      options.Service = "***";
      options.ProfileName = "***";
      options.Token = "***";
  });
```

For more information on AWS credentials and named profiles, see [AWS Credentials](https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/net-dg-config-creds.html#creds-locate) and [Named profiles](https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-files.html#cli-configure-files-using-profiles) in the AWS documentation.

## Configure serialization

Grain storage serialization is configured using the <xref:Orleans.Storage.IGrainStorageSerializer> interface. By default, grain state serializes using `Newtonsoft.Json`. You can customize the serializer by setting the <xref:Orleans.Configuration.DynamoDBStorageOptions.GrainStorageSerializer> property. For more information on configuring grain storage serializers, see [Grain storage serializers](../../host/configuration-guide/serialization.md#grain-storage-serializers).

:::zone-end
:::zone target="docs" pivot="orleans-3-x"

``` csharp
siloBuilder.AddDynamoDBGrainStorage(
  name: "profileStore",
  configureOptions: options =>
  {
      options.UseJson = true;
      options.AccessKey = "***";
      options.SecretKey = "***";
      options.Service = "***";
      options.ProfileName = "***";
      options.Token = "***";
  });
```

For more information on AWS credentials and named profiles, see [AWS Credentials](https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/net-dg-config-creds.html#creds-locate) and [Named profiles](https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-files.html#cli-configure-files-using-profiles) in the AWS documentation.

:::zone-end
