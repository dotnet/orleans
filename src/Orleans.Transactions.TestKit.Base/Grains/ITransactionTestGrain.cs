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
        /// <summary>
        /// create scope from client and run transactional grain
        /// </summary>
        /// <param name="newValue"></param>
        /// <returns></returns>
        Task CreateScopeAndSetValueWithoutAmbientTransaction(int newValue);

        /// <summary>
        /// create scope from client and run transactional grain, fail transaction with client
        /// </summary>
        /// <param name="newValue"></param>
        /// <returns></returns>
        Task CreateScopeAndSetValueAndFailWithoutAmbientTransaction(int newValue);

        /// <summary>
        /// create scope from client and run transactional grain
        /// </summary>
        /// <param name="newValue"></param>
        /// <returns></returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task CreateScopeAndSetValueWithAmbientTransaction(int newValue);

        /// <summary>
        /// create scope from client and run transactional grain, fail transaction with client
        /// </summary>
        /// <param name="newValue"></param>
        /// <returns></returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task CreateScopeAndSetValueAndFailWithAmbientTransaction(int newValue);

        Task Deactivate();
    }
}
