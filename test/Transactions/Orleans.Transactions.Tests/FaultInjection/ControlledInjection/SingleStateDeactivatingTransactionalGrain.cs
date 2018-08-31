using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.Tests.DeactivatingInjection;

namespace Orleans.Transactions.Tests.DeactivationTransaction
{

    public interface IFaultInjectionTransactionTestGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.CreateOrJoin)]
        Task Set(int newValue);

        [Transaction(TransactionOption.CreateOrJoin)]
        Task Add(int numberToAdd, FaultInjectionControl faultInjectionControl = null);

        [Transaction(TransactionOption.CreateOrJoin)]
        Task<int> Get();

        Task Deactivate();
    }

    public class SingleStateFaultInjectionTransactionalGrain : Grain, IFaultInjectionTransactionTestGrain
    {
        private readonly IFaultInjectionTransactionalState<GrainData> data;

        public SingleStateFaultInjectionTransactionalGrain(
            [FaultInjectionTransactionalState("data", TransactionTestConstants.TransactionStore)]
            IFaultInjectionTransactionalState<GrainData> data)
        {
            this.data = data;
        }

        public Task Set(int newValue)
        {
            return this.data.PerformUpdate(d => d.Value = newValue);
        }

        public Task Add(int numberToAdd, FaultInjectionControl faultInjectionControl = null)
        {
            //reset in case control from last tx isn't cleared for some reason
            faultInjectionControl.Reset();
            //dont replace it with this.data.FaultInjectionControl = faultInjectionControl, 
            //this.data.FaultInjectionControl must remain the same reference
            if (faultInjectionControl != null)
            {
                this.data.FaultInjectionControl.FaultInjectionPhase = faultInjectionControl.FaultInjectionPhase;
                this.data.FaultInjectionControl.FaultInjectionType = faultInjectionControl.FaultInjectionType;
            }
           
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
