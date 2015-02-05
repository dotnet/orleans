using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LoadTestGrainInterfaces;
using Orleans;

namespace LoadTestGrains
{
    public class MockStreamProviderControlGrain : Grain, IMockStreamProviderControlGrain
    {
        private bool _isTestRunning;

        public Task StartProducing()
        {
            _isTestRunning = true;
            return TaskDone.Done;
        }

        public Task<bool> ShouldProviderProduceEvents()
        {
            return Task.FromResult(_isTestRunning);
        }
    }
}
