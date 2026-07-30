namespace TransactionalExample.Grains;

[Reentrant]
public class AccountGrain : Grain, IAccountGrain
{
    private readonly ITransactionalState<Balance> _balance;

    public AccountGrain(
        [TransactionalState(nameof(balance))]
        ITransactionalState<Balance> balance) =>
        _balance = balance ?? throw new ArgumentNullException(nameof(balance));

    public Task Deposit(decimal amount) =>
        _balance.PerformUpdate(
            balance => balance.Value += amount);

    public Task Withdraw(decimal amount) =>
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

    public Task<decimal> GetBalance() =>
        _balance.PerformRead(balance => balance.Value);
}
