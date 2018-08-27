
using System.Threading.Tasks;

namespace Orleans.Transactions.Tests
{
    public interface ITransactionCommitterTestGrain : IGrainWithGuidKey
    {
        /// <summary>
        /// Commit transaction
        /// </summary>
        /// <param name="newValue"></param>
        /// <returns></returns>
        [Transaction(TransactionOption.Join)]
        Task Commit(string data);
    }
}
