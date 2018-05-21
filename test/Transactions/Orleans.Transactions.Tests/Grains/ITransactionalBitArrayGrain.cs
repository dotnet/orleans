
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
        [Transaction(TransactionOption.Required)]
        Task SetBit(int newValue);

        /// <summary>
        /// apply get operation to every transaction state
        /// </summary>
        /// <returns></returns>
        [Transaction(TransactionOption.Required)]
        Task<int[][]> Get();
    }
}
