using Orleans;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BenchmarkGrainInterfaces.Transaction
{
    public interface ITransactionRootGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.RequiresNew)]
        Task Run(List<int> grains);
    }
}
