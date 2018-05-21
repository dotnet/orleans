using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Transactions.Tests.Correctness;

namespace Orleans.Transactions.Tests
{
    public interface ITransactionCoordinatorGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.RequiresNew)]
        Task MultiGrainSet(List<ITransactionTestGrain> grains, int numberToAdd);

        [Transaction(TransactionOption.RequiresNew)]
        Task MultiGrainAdd(List<ITransactionTestGrain> grains, int numberToAdd);

        [Transaction(TransactionOption.RequiresNew)]
        Task MultiGrainDouble(List<ITransactionTestGrain> grains);

        [Transaction(TransactionOption.RequiresNew)]
        Task OrphanCallTransaction(ITransactionTestGrain grain);

        [Transaction(TransactionOption.RequiresNew)]
        Task AddAndThrow(ITransactionTestGrain grain, int numberToAdd);

        [Transaction(TransactionOption.RequiresNew)]
        Task MultiGrainAddAndThrow(ITransactionTestGrain grain, List<ITransactionTestGrain> grains, int numberToAdd);

        [Transaction(TransactionOption.RequiresNew)]
        Task MultiGrainSetBit(List<ITransactionalBitArrayGrain> grains, int bitIndex);
    }
}
