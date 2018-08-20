using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Transactions.Tests.DeactivatingInjection;

namespace Orleans.Transactions.Tests.DeactivationTransaction
{
    public interface IDeactivatingTransactionCoordinatorGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.Create)]
        Task MultiGrainSet(List<IDeactivatingTransactionTestGrain> grains, int numberToAdd);

        [Transaction(TransactionOption.Create)]
        Task MultiGrainAddAndDeactivate(List<IDeactivatingTransactionTestGrain> grains, int numberToAdd, 
            TransactionDeactivationPhase deactivationPhase = TransactionDeactivationPhase.None);
    }
    public class DeactivatingTransactionCoordinatorGrain : Grain, IDeactivatingTransactionCoordinatorGrain
    {
        public Task MultiGrainSet(List<IDeactivatingTransactionTestGrain> grains, int newValue)
        {
            return Task.WhenAll(grains.Select(g => g.Set(newValue)));
        }

        public Task MultiGrainAddAndDeactivate(List<IDeactivatingTransactionTestGrain> grains, int numberToAdd, 
            TransactionDeactivationPhase deactivationPhase = TransactionDeactivationPhase.None)
        {
            return Task.WhenAll(grains.Select(g => g.Add(numberToAdd, deactivationPhase)));
        }
    }
}
