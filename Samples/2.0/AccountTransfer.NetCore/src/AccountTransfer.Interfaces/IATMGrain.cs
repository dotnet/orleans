using System;
using System.Threading.Tasks;
using Orleans;

namespace AccountTransfer.Interfaces
{
    public interface IATMGrain : IGrainWithIntegerKey
    {
        [Transaction(TransactionOption.Create)]
        Task Transfer(Guid fromAccount, Guid toAccount, uint amountToTransfer);
    }
}
