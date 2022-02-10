using AccountTransfer.Interfaces;
using Orleans;
using Orleans.Concurrency;

namespace AccountTransfer.Grains;

[StatelessWorker]
public class AtmGrain : Grain, IAtmGrain
{
    public Task Transfer(
        IAccountGrain fromAccount,
        IAccountGrain toAccount,
        uint amountToTransfer) =>
        Task.WhenAll(
            fromAccount.Withdraw(amountToTransfer),
            toAccount.Deposit(amountToTransfer));
}
