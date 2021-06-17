using System;
using System.Threading.Tasks;
using Orleans;

namespace AccountTransfer.Interfaces
{
    public interface IAtmGrain : IGrainWithIntegerKey
    {
        [Transaction(TransactionOption.Create)]
        Task Transfer(IAccountGrain fromAccount, IAccountGrain toAccount, uint amountToTransfer);
    }
}
