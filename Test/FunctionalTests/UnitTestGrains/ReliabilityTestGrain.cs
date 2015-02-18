using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;


using UnitTestGrainInterfaces;

namespace UnitTestGrains
{
    public interface IReliabilityTestGrainState : IGrainState
    {
        //[Queryable]
        string Label { get; set; }
    }

    public class ReliabilityTestGrain : Grain<IReliabilityTestGrainState>, IReliabilityTestGrain
    {
        private TraceLogger logger;

        public override Task OnActivateAsync()
        {
            logger = TraceLogger.GetLogger("ReliabilityTestGrain", TraceLogger.LoggerType.Application);
            logger.Info("Activated grain {0} on silo {1}", Identity, this.RuntimeIdentity);
            return TaskDone.Done;
        }

        #region Implementation of IReliabilityTestGrain

        Task<string> IReliabilityTestGrain.GetLabel() { return Task.FromResult(State.Label); }

        public Task<IReliabilityTestGrain> GetOther() { return Task.FromResult<IReliabilityTestGrain>(null); }

        
        public Task SetLabels(string label, int delay)
        {
            logger.Info("{0}: changing label from {1} to {2}", Identity, State.Label, label);
            State.Label = label;
            Thread.Sleep(delay);
            return TaskDone.Done;
        }

        public Task SetLabel(string label)
        {
            logger.Info("{0}: changing label from {1} to {2}", Identity, State.Label, label);
            State.Label = label;
            return TaskDone.Done;
        }

        #endregion
    }
}
