using Orleans;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BenchmarkGrainInterfaces.Transaction
{
    public interface ITransactionRootGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.Create)]
        Task Run(List<int> grains);
    }
}
