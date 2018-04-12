using System.Threading.Tasks;

namespace Orleans.Transactions.Tests
{
    public interface ITransactionTestGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.Required)]
        Task Set(int newValue);

        [Transaction(TransactionOption.Required)]
        Task<int> Add(int numberToAdd);

        [Transaction(TransactionOption.Required)]
        Task<int> Get();

        [Transaction(TransactionOption.Required)]
        Task<int> AddAndThrow(int numberToAdd);

        Task Deactivate();
    }
}
