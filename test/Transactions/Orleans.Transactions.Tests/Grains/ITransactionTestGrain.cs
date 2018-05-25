using System.Threading.Tasks;

namespace Orleans.Transactions.Tests
{
    public interface ITransactionTestGrain : IGrainWithGuidKey
    {

        /// <summary>
        /// apply set operation to every transaction state
        /// </summary>
        /// <param name="newValue"></param>
        /// <returns></returns>
        [Transaction(TransactionOption.Required)]
        Task Set(int newValue);

        /// <summary>
        /// apply add operation to every transaction state
        /// </summary>
        /// <param name="numberToAdd"></param>
        /// <returns></returns>
        [Transaction(TransactionOption.Required)]
        Task<int[]> Add(int numberToAdd);

        /// <summary>
        /// apply get operation to every transaction state
        /// </summary>
        /// <returns></returns>
        [Transaction(TransactionOption.Required)]
        Task<int[]> Get();

        [Transaction(TransactionOption.Required)]
        Task AddAndThrow(int numberToAdd);

        Task Deactivate();
    }
}
