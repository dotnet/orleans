using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class AgentTestGrain : Grain, IAgentTestGrain
    {
        private readonly TestDedicatedAsynchAgent testAgent;

        public AgentTestGrain(IServiceProvider services)
        {
            this.testAgent = services.GetRequiredService<TestDedicatedAsynchAgent>();
        }

        public Task<int> GetFailureCount()
        {
            return Task.FromResult(this.testAgent.FailureCount);
        }
    }

    internal class TestDedicatedAsynchAgent : DedicatedAsynchAgent, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly ILogger logger;

        public int FailureCount { get; private set; }

        public TestDedicatedAsynchAgent(ExecutorService executorService, ILoggerFactory loggerFactory) : base(executorService, loggerFactory)
        {
            OnFault = FaultBehavior.RestartOnFault;
            this.logger = loggerFactory.CreateLogger<TestDedicatedAsynchAgent>();
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe<TestDedicatedAsynchAgent>(ServiceLifecycleStage.Active, OnStart);
        }

        protected override void Run()
        {
            RunAsync().GetAwaiter().GetResult();
        }

        private async Task RunAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            this.logger.LogInformation("Failure");
            this.FailureCount++;
            throw new ApplicationException();
        }

        private Task OnStart(CancellationToken ct)
        {
            this.logger.LogInformation("Starting");
            this.Start();
            return Task.CompletedTask;
        }
    }
}
