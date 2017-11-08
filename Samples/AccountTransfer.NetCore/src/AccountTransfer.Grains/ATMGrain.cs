using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using AccountTransfer.Interfaces;

namespace AccountTransfer.Grains
{
    [StatelessWorker]
    public class ATMGrain : Grain, IATMGrain
    {
        Task IATMGrain.Transfer(Guid fromAccount, Guid toAccount, uint ammountToTransfer)
        {
            return Task.WhenAll(
                this.GrainFactory.GetGrain<IAccountGrain>(fromAccount).Withdrawal(ammountToTransfer),
                this.GrainFactory.GetGrain<IAccountGrain>(toAccount).Deposit(ammountToTransfer));
        }
    }
}
