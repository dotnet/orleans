using System.Threading.Tasks;
using Orleans;

namespace BenchmarkGrainInterfaces.Transaction
{
    public interface ITransactionGrain : IGrainWithIntegerKey
    {
        [Transaction(TransactionOption.Required)]
        Task Run();
    }
}
