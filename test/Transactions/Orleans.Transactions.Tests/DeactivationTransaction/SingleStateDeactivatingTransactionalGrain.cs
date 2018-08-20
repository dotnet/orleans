using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.Tests.DeactivatingInjection;

namespace Orleans.Transactions.Tests.DeactivationTransaction
{

    public interface IDeactivatingTransactionTestGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.CreateOrJoin)]
        Task Set(int newValue);

        [Transaction(TransactionOption.CreateOrJoin)]
        Task Add(int numberToAdd, TransactionDeactivationPhase deactivationPhase = TransactionDeactivationPhase.None);

        [Transaction(TransactionOption.CreateOrJoin)]
        Task<int> Get();

        Task Deactivate();
    }

    public class SingleStateDeactivatingTransactionalGrain : Grain, IDeactivatingTransactionTestGrain
    {
        private readonly IDeactivationTransactionalState<GrainData> data;

        public SingleStateDeactivatingTransactionalGrain(
            [DeactivationTransactionalState("data", TransactionTestConstants.TransactionStore)]
            IDeactivationTransactionalState<GrainData> data)
        {
            this.data = data;
        }

        public Task Set(int newValue)
        {
            return this.data.PerformUpdate(d => d.Value = newValue);
        }

        public Task Add(int numberToAdd, TransactionDeactivationPhase deactivationPhase = TransactionDeactivationPhase.None)
        {
            this.data.DeactivationPhase = deactivationPhase;
            return this.data.PerformUpdate(d => d.Value += numberToAdd);
        }

        public Task<int> Get()
        {
            return this.data.PerformRead<int>(d => d.Value);
        }

        public Task Deactivate()
        {
            this.DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }
}
