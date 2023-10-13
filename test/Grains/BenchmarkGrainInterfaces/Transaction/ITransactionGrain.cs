namespace BenchmarkGrainInterfaces.Transaction
{
    public interface ITransactionGrain : IGrainWithIntegerKey
    {
        [Transaction(TransactionOption.CreateOrJoin)]
        Task Run();
    }
}
