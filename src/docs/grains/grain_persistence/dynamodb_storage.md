---
layout: page
title: Amazon DynamoDB Grain Persistence
---

# Amazon DynamoDB Grain Persistence

## Installation

Install the [`Microsoft.Orleans.Persistence.DynamoDB`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.DynamoDB) package from NuGet.

## Configuration

Configure the Dynamo DB grain persistence provider using the `ISiloBuilder.AddDynamoDBGrainStorage` extension methods.

``` csharp
siloBuilder.AddDynamoDBGrainStorage(
    name: "profileStore",
    configureOptions: options =>
    {
        options.UseJson = true;
        options.AccessKey = /* Dynamo DB access key */;
        options.SecretKey = /* Dynamo DB secret key */;
        options.Service = /* Dynamo DB service name */;
    });
```
