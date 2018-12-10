
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit.Correctnesss
{
    public interface ITransactionalBitArrayGrain : IGrainWithGuidKey
    {
        /// <summary>
        /// Ping 
        /// </summary>
        /// <returns></returns>
        Task Ping();
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
