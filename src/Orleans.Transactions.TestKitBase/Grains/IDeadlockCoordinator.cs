using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.Transactions.TestKit.Base.Grains
{
    public interface IDeadlockCoordinator : IGrainWithIntegerKey
    {
        [Transaction(TransactionOption.Create)]
        Task RunOrdered(params  int[] ids);
    }
}