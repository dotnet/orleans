// <transactions_account_grain_interface>
namespace TransactionalExample.Abstractions;

public interface IAccountGrain : IGrainWithStringKey
{
    [Transaction(TransactionOption.Join)]
    Task Withdraw(decimal amount);

    [Transaction(TransactionOption.Join)]
    Task Deposit(decimal amount);

    [Transaction(TransactionOption.CreateOrJoin)]
    Task<decimal> GetBalance();
}
// </transactions_account_grain_interface>
