using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Transactions.Tests.Correctness;

namespace Orleans.Transactions.Tests
{
    public interface ITransactionCoordinatorGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.Create)]
        Task MultiGrainSet(List<ITransactionTestGrain> grains, int numberToAdd);

        [Transaction(TransactionOption.Create)]
        Task MultiGrainAdd(List<ITransactionTestGrain> grains, int numberToAdd);

        [Transaction(TransactionOption.Create)]
        Task MultiGrainDouble(List<ITransactionTestGrain> grains);

        [Transaction(TransactionOption.Create)]
        Task OrphanCallTransaction(ITransactionTestGrain grain);

        [Transaction(TransactionOption.Create)]
        Task AddAndThrow(ITransactionTestGrain grain, int numberToAdd);

        [Transaction(TransactionOption.Create)]
        Task MultiGrainAddAndThrow(List<ITransactionTestGrain> grain, List<ITransactionTestGrain> grains, int numberToAdd);

        [Transaction(TransactionOption.Create)]
        Task MultiGrainSetBit(List<ITransactionalBitArrayGrain> grains, int bitIndex);
    }
}
