namespace AccountTransfer.Interfaces;

public interface IAccountGrain : IGrainWithStringKey
{
    [Transaction(TransactionOption.Join)]
    Task Withdraw(int amount);

    [Transaction(TransactionOption.Join)]
    Task Deposit(int amount);

    [Transaction(TransactionOption.CreateOrJoin)]
    Task<int> GetBalance();
}
