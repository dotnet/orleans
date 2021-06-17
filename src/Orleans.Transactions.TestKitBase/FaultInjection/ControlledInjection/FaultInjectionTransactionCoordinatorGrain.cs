using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit
{
    public interface IFaultInjectionTransactionCoordinatorGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.Create)]
        Task MultiGrainSet(List<IFaultInjectionTransactionTestGrain> grains, int numberToAdd);

        [Transaction(TransactionOption.Create)]
        Task MultiGrainAddAndFaultInjection(List<IFaultInjectionTransactionTestGrain> grains, int numberToAdd, 
            FaultInjectionControl faultInjection = null);
    }
    public class FaultInjectionTransactionCoordinatorGrain : Grain, IFaultInjectionTransactionCoordinatorGrain
    {
        public Task MultiGrainSet(List<IFaultInjectionTransactionTestGrain> grains, int newValue)
        {
            return Task.WhenAll(grains.Select(g => g.Set(newValue)));
        }

        public Task MultiGrainAddAndFaultInjection(List<IFaultInjectionTransactionTestGrain> grains, int numberToAdd,
            FaultInjectionControl faultInjection = null)
        {
            return Task.WhenAll(grains.Select(g => g.Add(numberToAdd, faultInjection)));
        }
    }
}
