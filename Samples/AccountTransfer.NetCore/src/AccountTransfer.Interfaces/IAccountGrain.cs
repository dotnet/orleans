using System.Threading.Tasks;
using Orleans;

namespace AccountTransfer.Interfaces
{
    public interface IAccountGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.Required)]
        Task Withdrawal(uint ammount);

        [Transaction(TransactionOption.Required)]
        Task Deposit(uint ammount);

        [Transaction(TransactionOption.Required)]
        Task<uint> GetBalance();
    }
}
