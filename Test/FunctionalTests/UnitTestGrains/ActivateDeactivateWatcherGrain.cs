using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTestGrainInterfaces;

namespace UnitTestGrains
{
    internal class ActivateDeactivateWatcherGrain : Grain, IActivateDeactivateWatcherGrain
    {
        private readonly Logger logger;

        private readonly List<ActivationId> activationCalls = new List<ActivationId>();
        private readonly List<ActivationId> deactivationCalls = new List<ActivationId>();

        public Task<ActivationId[]> GetActivateCalls() { return Task.FromResult(activationCalls.ToArray()); }
        public Task<ActivationId[]> GetDeactivateCalls() { return Task.FromResult(deactivationCalls.ToArray()); }

        public ActivateDeactivateWatcherGrain()
        {
            this.logger = GetLogger();
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