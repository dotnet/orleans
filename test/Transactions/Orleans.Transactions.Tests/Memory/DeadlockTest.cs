using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.Transactions.DeadlockDetection;
using Orleans.Transactions.TestKit;
using Orleans.Transactions.TestKit.Base.Grains;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests.Memory
{
    public class DeadlockFixture : MemoryTransactionsFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            builder.AddSiloBuilderConfigurator<DeadlockConfigurator>();
            builder.Options.InitialSilosCount = 2;
        }

        public class DeadlockConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder
                    .ConfigureServices(services =>
                        services.UseTransactionalDeadlockDetection()
                    );
            }
        }
    }

    public class DeadlockTest : TransactionTestRunnerBase, IClassFixture<DeadlockFixture>
    {
        private readonly IClusterClient client;

        public DeadlockTest(DeadlockFixture fixture, ITestOutputHelper output) : base(fixture.GrainFactory, output.WriteLine)
        {
            this.client = fixture.Client;
        }

        [Fact]
        public async Task CauseDeadlock()
        {
            var coordinator = this.grainFactory.GetGrain<IDeadlockCoordinator>(0);
            var tasks = new Task[]
            {
                coordinator.RunOrdered(0, 1, 2, 3),
                coordinator.RunOrdered(3, 2, 1, 0),
            };
            bool threw = false;
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception e)
            {
                this.testOutput($"threw {e}");
                threw = true;
            }

            Assert.True(threw, "bad ordering should throw!");
        }

        [Fact]
        public async Task OrderedAccessWorks()
        {
            var coordinator = this.grainFactory.GetGrain<IDeadlockCoordinator>(0);
            var tasks = new[] {coordinator.RunOrdered(0, 1), coordinator.RunOrdered(0, 1)};
            await Task.WhenAll(tasks);
        }


        private int[] Shuffle(int[] input)
        {
            var random = new Random();
            var output = new int[input.Length];
            Array.Copy(input, output, input.Length);
            for (int i = 0; i < output.Length; i++)
            {
                int j = random.Next(output.Length);
                int tmp = output[j];
                output[j] = output[i];
                output[i] = tmp;
            }

            return output;
        }

    }
}