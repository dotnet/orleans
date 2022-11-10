using AccountTransfer.Interfaces;
using Orleans.Concurrency;
using Orleans.Transactions.Abstractions;

namespace AccountTransfer.Grains;

[GenerateSerializer]
public record class Balance
{
    [Id(0)]
    public int Value { get; set; } = 1_000;
}

[Reentrant]
public sealed class AccountGrain : Grain, IAccountGrain
{
    private readonly ITransactionalState<Balance> _balance;

    public AccountGrain(
        [TransactionalState("balance")] ITransactionalState<Balance> balance) =>
        _balance = balance ?? throw new ArgumentNullException(nameof(balance));

    public Task Deposit(int amount) =>
        _balance.PerformUpdate(
            balance => balance.Value += amount);

    public Task Withdraw(int amount) =>
        _balance.PerformUpdate(balance =>
        {
            if (balance.Value < amount)
            {
                throw new InvalidOperationException(
                    $"Withdrawing {amount} credits from account " +
                    $"\"{this.GetPrimaryKeyString()}\" would overdraw it." +
                    $" This account has {balance.Value} credits.");
            }

            balance.Value -= amount;
        });

    public Task<int> GetBalance() =>
        _balance.PerformRead(balance => balance.Value);
}
