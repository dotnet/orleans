namespace AccountTransfer.Interfaces;

public interface IAtmGrain : IGrainWithIntegerKey
{
    [Transaction(TransactionOption.Create)]
    Task Transfer(
        IAccountGrain fromAccount,
        IAccountGrain toAccount,
        int amountToTransfer);
}
