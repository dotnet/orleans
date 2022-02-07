# BankAccount - ACID transactions between grains

This sample demonstrates how to implement ACID transactions using Orleans using a bank account scenario.
There are two kinds of grains:

![BankClient application running in a terminal](./assets/BankClient.png)

* `AccountGrain`, which implements `IAccountGrain`, simulates a bank account with an balance.
* `AtmGrain`, which implements `IAtmGrain`, simulates an Automatic Teller Machine which allows transfers between two bank accounts.

`AtmGrain` has this interface:

```csharp
public interface IAtmGrain : IGrainWithIntegerKey
{
    [Transaction(TransactionOption.Create)]
    Task Transfer(Guid fromAccount, Guid toAccount, uint amountToTransfer);
}
```

`AccountGrain` has this interface:

```csharp
public interface IAccountGrain : IGrainWithGuidKey
{
    [Transaction(TransactionOption.Join)]
    Task Withdraw(uint amount);

    [Transaction(TransactionOption.Join)]
    Task Deposit(uint amount);

    [Transaction(TransactionOption.CreateOrJoin)]
    Task<uint> GetBalance();
}
```

The `[Transaction(option)]` attributes on the grain methods tell the runtime that these methods are transactional.
The `IAtmGrain.Transfer` method creates a transation, while the `IAccountGrain.Withdraw` and `IAccountGrain.Deposit` methods must be called in the context of an existing transactions.

`AtmGrain.Transfer(...)` is implemented as follows:

```csharp
public async Task Transfer(IAccountGrain fromAccount, IAccountGrain toAccount, uint amountToTransfer)
{
    await Task.WhenAll(
        fromAccount.Withdraw(amountToTransfer),
        toAccount.Deposit(amountToTransfer));
}
```

The `Transfer` method withdraws the specified amount from one `IAccountGrain` and deposits it in the other. Orleans ensures that this occurs in the context of a transaction to ensure consistency.

The `AccountGrain.Deposit` method adds the deposited amount to the account balance using the `ITransactionalState<T>.PerformUpdate` method:

```csharp
public Task Deposit(uint amount) => _balance.PerformUpdate(x => x.Value += amount);
```

Real banks allow overdrawing accounts, but this sample does not. `AccountGrain.Withdraw(uint amount)` prevents overdrawing by throwing an exception, causing the transaction to be aborted:

```csharp
public Task Withdraw(uint amount) => _balance.PerformUpdate(x =>
{
    if (x.Value < amount)
    {
        throw new InvalidOperationException(
            $"Withdrawing {amount} credits from account \"{this.GetPrimaryKeyString()}\" would overdraw it."
            + $" This account has {x.Value} credits.");
    }

    x.Value -= amount;
});
```

## Running the sample

First start the *BankServer* process

``` bash
dotnet run --project BankServer
```

Then start the *BankClient* process

``` bash
dotnet run --project BankClient
```

The client will issue transactions between random accounts in a loop, printing the results. For example:

```
We transfered 100 credits from Pasqualino to Ida.
Pasqualino balance: 1500
Ida balance: 1600
```

When a withdraw would overdraw an account, the client will print an error like so:

```
Error transfering 100 credits from Derick to Xaawo: Transaction 2edc92f5-a94d-4167-9522-fa661cc030ff Aborted because of an unhandled exception in a grain method call. See InnerException for details.
        InnerException: Withdrawing 100 credits from account "Derick" would overdraw it. This account has 0 credits.
```
