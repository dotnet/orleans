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
            this.balance.State.Value += amount;
            this.balance.Save();
            return Task.CompletedTask;
        }

        Task IAccountGrain.Withdraw(uint amount)
        {
            this.balance.State.Value -= amount;
            this.balance.Save();
            return Task.CompletedTask;
        }

        Task<uint> IAccountGrain.GetBalance()
        {
            return Task.FromResult(this.balance.State.Value);
        }
    }
}
