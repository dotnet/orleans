using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal class ActivateDeactivateWatcherGrain : Grain, IActivateDeactivateWatcherGrain
    {
        private Logger logger;

        private readonly List<string> activationCalls = new List<string>();
        private readonly List<string> deactivationCalls = new List<string>();

        public Task<string[]> GetActivateCalls() { return Task.FromResult(activationCalls.ToArray()); }
        public Task<string[]> GetDeactivateCalls() { return Task.FromResult(deactivationCalls.ToArray()); }

        public override Task OnActivateAsync()
        {
            this.logger = this.GetLogger();
            return base.OnActivateAsync();
        }

        public Task Clear()
        {
            if (logger.IsVerbose) logger.Verbose("Clear");
            activationCalls.Clear();
            deactivationCalls.Clear();
            return Task.CompletedTask;
        }
        public Task RecordActivateCall(string activation)
        {
            if (logger.IsVerbose) logger.Verbose("RecordActivateCall: " + activation);
            activationCalls.Add(activation);
            return Task.CompletedTask;
        }

        public Task RecordDeactivateCall(string activation)
        {
            if (logger.IsVerbose) logger.Verbose("RecordDeactivateCall: " + activation);
            deactivationCalls.Add(activation);
            return Task.CompletedTask;
        }
    }
}
