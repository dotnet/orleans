using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;
using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers;
using Orleans.TestingHost.Utils;
using Orleans.Hosting;

namespace Orleans.Transactions.Tests
{
    public class TransactionRecoveryTestsRunner : TransactionTestRunnerBase
    {
        private readonly Random seed;
        private readonly TestCluster testCluster;
        private readonly ILogger logger;
        private static TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(60);
        public TransactionRecoveryTestsRunner(TestCluster testCluster, ITestOutputHelper output)
            : base(testCluster.GrainFactory, output)
        {
            this.testCluster = testCluster;
            this.logger = this.testCluster.ServiceProvider.GetService<ILogger<TransactionRecoveryTestsRunner>>();
            this.seed = new Random();
        }

        // this is only needed for singleTM tests
        public class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.ConfigureServices(services => services.AddTransientNamedService<IControllable, TMGrainLocator>(typeof(TMGrainLocator).Name));
            }
        }

        public virtual async Task TransactionWillRecoverAfterRandomSiloFailure(string transactionTestGrainClassName)
        {
            const int grainCount = 100;
            var txGrains = Enumerable.Range(0, grainCount)
                .Select(i => RandomTestGrain(transactionTestGrainClassName))
                .ToList();
            var txSucceedBeforeInterruption = await AllTxSucceed(txGrains);
            this.logger.LogInformation($"Tx succeed before interruption : {txSucceedBeforeInterruption}");
            //randomly ungraceful shut down one silo
            this.testCluster.KillSilo(this.testCluster.Silos[this.seed.Next(this.testCluster.Silos.Count)]);
            await TestingUtils.WaitUntilAsync(lastTry => CheckTxResult(txGrains, lastTry), RecoveryTimeout);
        }

        //given a predicate, whether to kill a silo
        private async Task KillRandomSilo()
        {
            var mgmt = this.testCluster.GrainFactory.GetGrain<IManagementGrain>(0);

            object[] results = await mgmt.SendControlCommandToProvider(typeof(TMGrainLocator).FullName, typeof(TMGrainLocator).Name, 1);
            this.logger.LogInformation($"Current TMGrainLocator list : {String.Join(";", results.Select(re => re.As<SiloAddress>().ToString()))}");
            if (results.Length == 0)
                throw new Exception("No silo fits the predicate, potential test configuration issues");
            var murderTarget = results.ElementAt(this.seed.Next(results.Length)) as SiloAddress;
            this.logger.LogInformation($"Current hard kill target is {murderTarget}");
            foreach (var siloHanle in this.testCluster.Silos)
            {
                if(siloHanle.SiloAddress.Equals(murderTarget))
                    siloHanle.StopSilo(false);
            }
        }

        public class TMGrainLocator : IControllable
        {
            private ILocalSiloDetails siloDetails;
            public TMGrainLocator(ILocalSiloDetails siloDetails)
            {
                this.siloDetails = siloDetails;
            }

            public Task<object> ExecuteCommand(int command, object arg)
            {
                return Task.FromResult<object>(this.siloDetails.SiloAddress);
            }
        }

        private async Task<bool> CheckTxResult(IList<ITransactionTestGrain> txGrains, bool assertIsTrue)
        {
            var succeed = await AllTxSucceed(txGrains);
            this.logger.LogInformation($"All transactions succeed after interruption : {succeed}");
            if (assertIsTrue)
            {
                //consider it recovered if all tx succeed
                Assert.True(succeed);
                return true;
            }
            else
            {
                return succeed;
            }
        }

        private async Task<bool> AllTxSucceed(IEnumerable<ITransactionTestGrain> txGrains)
        {
            var tasks = new List<Task>();
            tasks.AddRange(txGrains.Select(txGrain => txGrain.Add(1)));
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception)
            {
                base.output.WriteLine($"Some transactions failed. {tasks.Count(t => t.IsFaulted)} out of {tasks.Count} failed");
                return false;
            }

            return true;
        }
    }
}
