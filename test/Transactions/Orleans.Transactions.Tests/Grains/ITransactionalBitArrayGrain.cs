
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions.Tests.Correctness
{
    public interface ITransactionalBitArrayGrain : IGrainWithGuidKey
    {
        /// <summary>
        /// apply set operation to every transaction state
        /// </summary>
        /// <param name="newValue"></param>
        /// <returns></returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task SetBit(int newValue);

        /// <summary>
        /// Performs a read transaction on each state, returning the results in order.
        /// </summary>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task<List<BitArrayState>> Get();
    }
}
