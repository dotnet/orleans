using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using TestInternalGrainInterfaces;

namespace TestInternalGrains
{
    internal class ActivateDeactivateWatcherGrain : Grain, IActivateDeactivateWatcherGrain
    {
        private Logger logger;

        private readonly List<ActivationId> activationCalls = new List<ActivationId>();
        private readonly List<ActivationId> deactivationCalls = new List<ActivationId>();

        public Task<ActivationId[]> GetActivateCalls() { return Task.FromResult(activationCalls.ToArray()); }
        public Task<ActivationId[]> GetDeactivateCalls() { return Task.FromResult(deactivationCalls.ToArray()); }

        public override Task OnActivateAsync()
        {
            this.logger = GetLogger();
            return base.OnActivateAsync();
        }

        public Task Clear()
        {
            if (logger.IsVerbose) logger.Verbose("Clear");
            activationCalls.Clear();
            deactivationCalls.Clear();
            return TaskDone.Done;
        }
        public Task RecordActivateCall(ActivationId activation)
        {
            if (logger.IsVerbose) logger.Verbose("RecordActivateCall: " + activation.ToFullString());
            activationCalls.Add(activation);
            return TaskDone.Done;
        }

        public Task RecordDeactivateCall(ActivationId activation)
        {
            if (logger.IsVerbose) logger.Verbose("RecordDeactivateCall: " + activation.ToFullString());
            deactivationCalls.Add(activation);
            return TaskDone.Done;
        }
    }
}