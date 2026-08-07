# Microsoft Orleans Transaction for DynamoDB

## Introduction
Microsoft Orleans Transaction for DynamoDB provides grain transaction for Microsoft Orleans using Amazon's DynamoDB.
This ensures that your grains can perform transactions in a distributed environment using DynamoDB as the underlying storage.

Serialized transactional state and metadata must each fit within DynamoDB's 400 KB item limit. Write batches are split to remain within DynamoDB's limits of 100 actions and 4 MB of affected items per transaction.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Transactions.DynamoDB
```
