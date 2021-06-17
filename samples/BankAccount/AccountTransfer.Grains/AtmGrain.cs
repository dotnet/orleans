using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using AccountTransfer.Interfaces;

namespace AccountTransfer.Grains
{
    [StatelessWorker]
    public class AtmGrain : Grain, IAtmGrain
    {
        public async Task Transfer(IAccountGrain fromAccount, IAccountGrain toAccount, uint amountToTransfer)
        {
            await Task.WhenAll(
                fromAccount.Withdraw(amountToTransfer),
                toAccount.Deposit(amountToTransfer));
        }
    }
}
