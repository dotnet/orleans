# Microsoft Orleans Transactions Provider for ADO.NET

This package stores Orleans transactional state in a relational database using ADO.NET. It supports SQL Server, MySQL, PostgreSQL, and Oracle.

## Install

```shell
dotnet add package Microsoft.Orleans.Transactions.AdoNet
```

Install the ADO.NET driver for your database separately, such as `Microsoft.Data.SqlClient`, `MySql.Data`, `MySqlConnector`, `Npgsql`, or `Oracle.ManagedDataAccess.Core`.

## Configure

Create the transaction tables using the script for your database:

- [SQL Server](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Transactions.AdoNet/SQLServer-Transactions.sql)
- [MySQL](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Transactions.AdoNet/MySQL-Transactions.sql)
- [PostgreSQL](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Transactions.AdoNet/PostgreSQL-Transactions.sql)
- [Oracle](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Transactions.AdoNet/Oracle-Transactions.sql)

Register the transactional state provider on every participating silo:

```csharp
siloBuilder
    .AddAdoNetTransactionalStateStorage(
        "TransactionStore",
        options =>
        {
            options.Invariant = "Microsoft.Data.SqlClient";
            options.ConnectionString = builder.Configuration.GetConnectionString("transactions")!;
        })
    .UseTransactions();
```

Reference the provider name when injecting transactional state:

```csharp
public AccountGrain(
    [TransactionalState("balance", "TransactionStore")]
    ITransactionalState<Balance> balance)
{
    _balance = balance;
}
```

For transaction semantics and grain examples, see the [Orleans transactions documentation](https://learn.microsoft.com/dotnet/orleans/grains/transactions).
