using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Transactions.Abstractions;
using AccountTransfer.Interfaces;

[assembly: GenerateSerializer(typeof(AccountTransfer.Grains.Balance))]

namespace AccountTransfer.Grains
{
    [Serializable]
    public class Balance
    {
        public uint Value { get; set; } = 1000;
    }

    public class AccountGrain : Grain, IAccountGrain
    {
        private readonly ITransactionalState<Balance> balance;

        public AccountGrain(
            [TransactionalState("balance")] ITransactionalState<Balance> balance)
        {
            this.balance = balance ?? throw new ArgumentNullException(nameof(balance));
        }

        Task IAccountGrain.Deposit(uint amount)
        {
            return this.balance.PerformUpdate(x => x.Value += amount);
        }

        Task IAccountGrain.Withdraw(uint amount)
        {
            return this.balance.PerformUpdate(x => x.Value -= amount);
        }

        Task<uint> IAccountGrain.GetBalance()
        {
            return this.balance.PerformRead(x => x.Value);
        }
    }
}
