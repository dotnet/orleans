# Microsoft Orleans Transactions for Azure Storage

## Introduction
Microsoft Orleans Transactions for Azure Storage provides the infrastructure to store Orleans transaction logs in Azure Storage. This package allows Orleans applications to use ACID transactions across multiple grain calls with Azure Storage as the backing transaction log store.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Transactions.AzureStorage
```

## Example - Configuring Azure Storage for Transactions
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Transactions;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Enable transactions
            .AddAzureTableTransactionalStateStorage(
                name: "TransactionStore",
                configureOptions: options =>
                {
                    options.ConnectionString = "YOUR_AZURE_STORAGE_CONNECTION_STRING";
                })
            .UseTransactions();
    });

// Run the host
await builder.RunAsync();
```

## Example - Using Transactions in Grains
```csharp
// A grain with transactional state
public class MyTransactionalGrain : Grain, IMyTransactionalGrain
{
    private readonly ITransactionalState<MyState> _state;

    // Inject the transactional state
    public MyTransactionalGrain(
        [TransactionalState("state", "TransactionStore")]
        ITransactionalState<MyState> state)
    {
        _state = state;
    }

    // Method that performs a transaction
    [Transaction(TransactionOption.Create)]
    public async Task Transfer(string otherGrainKey, int amount)
    {
        // Read our state within the transaction
        var myState = await _state.PerformRead(state => state);
        
        // Ensure we have enough balance
        if (myState.Balance < amount)
            throw new InvalidOperationException("Insufficient funds");
            
        // Update our state within the transaction
        await _state.PerformUpdate(s => s.Balance -= amount);
        
        // Call another grain within the same transaction
        var otherGrain = GrainFactory.GetGrain<IMyTransactionalGrain>(otherGrainKey);
        await otherGrain.Deposit(amount);
    }

    // Method that participates in a transaction
    [Transaction(TransactionOption.Join)]
    public Task Deposit(int amount)
    {
        // Update state within the joined transaction
        return _state.PerformUpdate(s => s.Balance += amount);
    }

    // Read operation within a transaction
    [Transaction(TransactionOption.CreateOrJoin)]
    public Task<int> GetBalance()
    {
        return _state.PerformRead(s => s.Balance);
    }
}

// State class

public class MyState
{
    public int Balance { get; set; }
}
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Transactions](https://learn.microsoft.com/en-us/dotnet/orleans/grains/transactions)
- [Distributed ACID Transactions](https://learn.microsoft.com/en-us/dotnet/orleans/grains/transactions/acid-transactions)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)