using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit
{
    public interface ITransactionTestGrain : IGrainWithGuidKey
    {

        /// <summary>
        /// apply set operation to every transaction state
        /// </summary>
        /// <param name="newValue"></param>
        /// <returns></returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task Set(int newValue);

        /// <summary>
        /// apply add operation to every transaction state
        /// </summary>
        /// <param name="numberToAdd"></param>
        /// <returns></returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task<int[]> Add(int numberToAdd);

        /// <summary>
        /// apply get operation to every transaction state
        /// </summary>
        /// <returns></returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task<int[]> Get();

        [Transaction(TransactionOption.CreateOrJoin)]
        Task AddAndThrow(int numberToAdd);

        Task Deactivate();
    }
}
