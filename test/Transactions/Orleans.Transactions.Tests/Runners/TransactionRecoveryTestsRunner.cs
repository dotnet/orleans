using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
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
        public TransactionRecoveryTestsRunner(TestCluster testCluster, ITestOutputHelper output, bool distributedTM = false)
            : base(testCluster.GrainFactory, output, distributedTM)
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

        //For distributedTM
        public virtual async Task TransactionWillRecoverAfterRandomSiloFailure(TransactionTestConstants.TransactionGrainStates transactionTestGrainClassName)
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

        //For singleTM , only in singleTM we have the need of test two scenarios, whic is kill silo which runs the TM and ramdom silo which doesn't 
        public virtual async Task TransactionWillRecoverAfterRandomSiloFailure(string transactionTestGrainClassName, bool killSiloWhichRunsTm)
        {
            const int grainCount = 100;
            var txGrains = Enumerable.Range(0, grainCount)
                .Select(i => RandomTestGrain(transactionTestGrainClassName))
                .ToList();
            var txSucceedBeforeInterruption = await AllTxSucceed(txGrains);
            this.logger.LogInformation($"Tx succeed before interruption : {txSucceedBeforeInterruption}");
            if(killSiloWhichRunsTm)
                await KillSilo(location => location.ActiveInCurrentSilo);
            else
            {
                await KillSilo(location => !location.ActiveInCurrentSilo);
            }
            await TestingUtils.WaitUntilAsync(lastTry => CheckTxResult(txGrains, lastTry), RecoveryTimeout);
        }

        //given a predicate, whether to kill a silo
        private async Task KillSilo(Func<TMGrainLocator.TMGrainLocation, bool> predicate)
        {
            var mgmt = this.testCluster.GrainFactory.GetGrain<IManagementGrain>(0);

            object[] results = await mgmt.SendControlCommandToProvider(typeof(TMGrainLocator).FullName, typeof(TMGrainLocator).Name, 1);
            this.logger.LogInformation($"Current TMGrainLocator list : {String.Join(";", results.Select(re => re.As<TMGrainLocator.TMGrainLocation>().ToString()))}");
            var murderCandidates = results.Where(re => predicate(re as TMGrainLocator.TMGrainLocation));
            if (murderCandidates.Count() == 0)
                throw new Exception("No silo fits the predicate, potential test configuration issues");
            var murderTarget = murderCandidates.ElementAt(this.seed.Next(murderCandidates.Count())) as TMGrainLocator.TMGrainLocation;
            this.logger.LogInformation($"Current hard kill target is {murderTarget}");
            foreach (var siloHanle in this.testCluster.Silos)
            {
                if(siloHanle.SiloAddress.Equals(murderTarget.CurrentSiloAddress))
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
                return Task.FromResult<object>(new TMGrainLocation()
                {
                    ActiveInCurrentSilo = TransactionManagerGrain.IsActive,
                    CurrentSiloAddress = this.siloDetails.SiloAddress
                });
            }

            public class TMGrainLocation
            {
                public bool ActiveInCurrentSilo { get; set; }
                public SiloAddress CurrentSiloAddress { get; set; }

                public override string ToString()
                {
                    return $"{nameof(ActiveInCurrentSilo)} : {this.ActiveInCurrentSilo}, {nameof(CurrentSiloAddress)} : {this.CurrentSiloAddress}";
                }
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
                return false;
            }

            return true;
        }
    }
}
