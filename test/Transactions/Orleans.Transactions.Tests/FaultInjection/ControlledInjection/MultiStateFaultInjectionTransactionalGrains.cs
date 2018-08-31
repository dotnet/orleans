using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.Tests.DeactivatingInjection;

namespace Orleans.Transactions.Tests.DeactivationTransaction
{
    public static class TransactionFaultInjectionGrainNames
    {
        public const string SingleStateFaultInjectionTransactionalGrain = "SingleStateFaultInjectionTransactionalGrain";
    }

    public interface IFaultInjectionTransactionTestGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.CreateOrJoin)]
        Task Set(int newValue);

        [Transaction(TransactionOption.CreateOrJoin)]
        Task Add(int numberToAdd, FaultInjectionControl faultInjectionControl = null);

        [Transaction(TransactionOption.CreateOrJoin)]
        Task<List<int>> Get();

        Task Deactivate();
    }

    public class SingleStateFaultInjectionTransactionalGrain : MultipleStateFaultInjectionTransactionalGrain
    {
        public SingleStateFaultInjectionTransactionalGrain(
            [FaultInjectionTransactionalState("data", TransactionTestConstants.TransactionStore)]
            IFaultInjectionTransactionalState<GrainData> data)
            :base(new List<IFaultInjectionTransactionalState<GrainData>>(){data})
        {
        }
    }

    public class MultipleStateFaultInjectionTransactionalGrain : Grain, IFaultInjectionTransactionTestGrain
    {
        private readonly List<IFaultInjectionTransactionalState<GrainData>> states;

        public MultipleStateFaultInjectionTransactionalGrain(
            List<IFaultInjectionTransactionalState<GrainData>> states)
        {
            this.states = states;
        }

        public Task Set(int newValue)
        {
            var tasks = new List<Task>();
            foreach (var state in states)
            {
                tasks.Add(state.PerformUpdate(d => d.Value = newValue));
            }
            return Task.WhenAll(tasks);
        }

        private void ResetFaultInjectionControl()
        {
            this.states.ForEach(state => state.FaultInjectionControl.FaultInjectionPhase = TransactionFaultInjectPhase.None);
            this.states.ForEach(state => state.FaultInjectionControl.FaultInjectionType = FaultInjectionType.None);
        }

        public Task Add(int numberToAdd, FaultInjectionControl faultInjectionControl = null)
        {
            //reset fault injection to none in case something isn't cleared last time. 
            ResetFaultInjectionControl();
            //dont replace it with this.data.FaultInjectionControl = faultInjectionControl, 
            //this.data.FaultInjectionControl must remain the same reference
            if (faultInjectionControl != null)
            {
                this.states[0].FaultInjectionControl.FaultInjectionPhase = faultInjectionControl.FaultInjectionPhase;
                this.states[0].FaultInjectionControl.FaultInjectionType = faultInjectionControl.FaultInjectionType;
            }

            var tasks = new List<Task>();
            foreach (var state in states)
            {
                tasks.Add(state.PerformUpdate(d => d.Value += numberToAdd));
            }
            return Task.WhenAll(tasks);
        }

        public async Task<List<int>> Get()
        {
            var results = new List<int>();
            foreach (var state in states)
            {
                results.Add(await state.PerformRead<int>(d => d.Value));
            }
            return results;
        }

        public Task Deactivate()
        {
            this.DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }
}
