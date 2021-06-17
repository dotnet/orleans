using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Transactions.Abstractions;
using AccountTransfer.Interfaces;

namespace AccountTransfer.Grains
{
    [Serializable]
    public class Balance
    {
        public uint Value { get; set; } = 1000;
    }

    public class AccountGrain : Grain, IAccountGrain
    {
        private readonly ITransactionalState<Balance> _balance;

        public AccountGrain(
            [TransactionalState("balance")] ITransactionalState<Balance> balance)
        {
            _balance = balance ?? throw new ArgumentNullException(nameof(balance));
        }

        public Task Deposit(uint amount) => _balance.PerformUpdate(x => x.Value += amount);

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

        public Task<uint> GetBalance() => _balance.PerformRead(x => x.Value);
    }
}
