using System;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit.Base.Grains
{
    public interface IDelayedGrain : IGrainWithIntegerKey
    {
        [Transaction(TransactionOption.Join)]
        Task UpdateState(TimeSpan delay, string newState);

        [Transaction(TransactionOption.CreateOrJoin)]
        Task ThrowException();
    }
}