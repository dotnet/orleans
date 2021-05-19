using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal class ActivateDeactivateWatcherGrain : Grain, IActivateDeactivateWatcherGrain
    {
        private ILogger logger;

        private readonly List<string> activationCalls = new List<string>();
        private readonly List<string> deactivationCalls = new List<string>();

        public ActivateDeactivateWatcherGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public Task<string[]> GetActivateCalls() { return Task.FromResult(activationCalls.ToArray()); }
        public Task<string[]> GetDeactivateCalls() { return Task.FromResult(deactivationCalls.ToArray()); }
        
        public Task Clear()
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Clear");
            activationCalls.Clear();
            deactivationCalls.Clear();
            return Task.CompletedTask;
        }
        public Task RecordActivateCall(string activation)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("RecordActivateCall: " + activation);
            activationCalls.Add(activation);
            return Task.CompletedTask;
        }

        public Task RecordDeactivateCall(string activation)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("RecordDeactivateCall: " + activation);
            deactivationCalls.Add(activation);
            return Task.CompletedTask;
        }
    }
}
