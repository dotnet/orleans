using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using Orleans.Hosting;
using Orleans.Transactions.Tests.Correctness;
using TestExtensions;

namespace Orleans.Transactions.Tests
{
    public class TransactionRecoveryTestsRunner : TransactionTestRunnerBase
    {
        private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(60);

        private readonly Random random;
        private readonly TestCluster testCluster;
        private readonly ILogger logger;

        private class ExpectedGrainActivity
        {
            public ITransactionalBitArrayGrain Grain { get; set; }
            public BitArrayState Expected { get; } = new BitArrayState();
        }

        public TransactionRecoveryTestsRunner(TestCluster testCluster, ITestOutputHelper output)
            : base(testCluster.GrainFactory, output)
        {
            this.testCluster = testCluster;
            this.logger = this.testCluster.ServiceProvider.GetService<ILogger<TransactionRecoveryTestsRunner>>();
            this.random = new Random();
        }

        public virtual Task TransactionWillRecoverAfterRandomSiloGracefulShutdown(string transactionTestGrainClassName)
        {
            return TransactionWillRecoverAfterRandomSiloFailure(transactionTestGrainClassName, true);
        }

        public virtual Task TransactionWillRecoverAfterRandomSiloUnGracefulShutdown(string transactionTestGrainClassName)
        {
            return TransactionWillRecoverAfterRandomSiloFailure(transactionTestGrainClassName, false);
        }

        protected virtual async Task TransactionWillRecoverAfterRandomSiloFailure(string transactionTestGrainClassName, bool gracefulShutdown)
        {
            const int grainCount = 100;
            int index = 0;
            Func<int> getIndex = () => { return index++; };
            List<ExpectedGrainActivity> txGrains = Enumerable.Range(0, grainCount)
                .Select(i => new ExpectedGrainActivity { Grain = RandomTestGrain<ITransactionalBitArrayGrain>(transactionTestGrainClassName) })
                .ToList();
            var txSucceedBeforeInterruption = await AllTxSucceed(txGrains, getIndex());
            await ValidateResults(txGrains);
            this.logger.LogInformation($"Tx succeed before interruption : {txSucceedBeforeInterruption}");
            // have transactions in flight when silo goes down
            bool killed = false;
            Task succeeding = RunWhileSucceeding(txGrains, getIndex, () => { return killed; });
            if (gracefulShutdown)
                this.testCluster.StopSilo(this.testCluster.Silos[this.random.Next(this.testCluster.Silos.Count)]);
            else
                this.testCluster.KillSilo(this.testCluster.Silos[this.random.Next(this.testCluster.Silos.Count)]);
            killed = true;
            await succeeding;
            await TestingUtils.WaitUntilAsync(lastTry => CheckTxResult(txGrains, getIndex, lastTry), RecoveryTimeout);
            output.WriteLine($"Performed {index} transactions");
            await ValidateResults(txGrains);
        }

        private async Task RunWhileSucceeding(IList<ExpectedGrainActivity> txGrains, Func<int> getIndex, Func<bool> killed)
        {
            while (!killed() && await AllTxSucceed(txGrains, getIndex())) { };
        }

        private async Task<bool> CheckTxResult(IList<ExpectedGrainActivity> txGrains, Func<int> getIndex, bool assertIsTrue)
        {
            var succeed = await AllTxSucceed(txGrains, getIndex());
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

        private async Task<bool> AllTxSucceed(IEnumerable<ExpectedGrainActivity> txGrains, int index)
        {
            var tasks = new List<Task>();
            tasks.AddRange(txGrains
                .Select((txGrain, i) => new { index = i, value = txGrain } )
                .GroupBy(v => v.index / 2)
                .Select(g => SetBit(g.Select(i => i.value).ToList(), index)));
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

        private async Task SetBit(List<ExpectedGrainActivity> grains, int index)
        {
            try
            {
                await this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid()).MultiGrainSetBit(grains.Select(v => v.Grain).ToList(), index);
            }
            catch (Exception e)
            {
                base.output.WriteLine($"Some transactions failed. Index: {index}: Exception: {e}");
                grains.ForEach(g => g.Expected.Set(index, false));
                throw;
            }
            grains.ForEach(g => g.Expected.Set(index, true));
        }

        private async Task ValidateResults(List<ExpectedGrainActivity> txGrains)
        {
            await ReportResults(txGrains);
            foreach (ExpectedGrainActivity activity in txGrains)
            {
                int[][] actual = await activity.Grain.Get();
                // self consistency check, all resources should be the same
                int[] first = actual.FirstOrDefault();
                Assert.NotNull(first);
                foreach (int[] result in actual)
                {
                    Assert.Equal(first.Length, result.Length);
                    for(int i = 0; i< first.Length; i++)
                    {
                        Assert.Equal(first[i], result[i]);
                    }
                }
                // Check against expected, only need to check first, since we've already verified all resources are the same.
                int[] expected = activity.Expected.Value;
                Assert.Equal(first.Length, expected.Length);
                for (int i = 0; i < first.Length; i++)
                {
                    Assert.Equal(first[i], expected[i]);
                }
            }
        }

        private async Task ReportResults(List<ExpectedGrainActivity> txGrains)
        {
            int i = 0;
            foreach (ExpectedGrainActivity activity in txGrains)
            {
                int[] expected = activity.Expected.Value;
                int[][] actual = await activity.Grain.Get();
                int[] first = actual.FirstOrDefault();
                if(first == null)
                {
                    output.WriteLine($"No activity for {i}");
                    return;
                }
                int j = 0;
                List<int> badIndexes;
                foreach (int[] result in actual)
                {
                    badIndexes = result.Select((v, idx) => v == first[idx] ? -1 : idx).Where(v => v != -1).ToList();
                    if(badIndexes.Count != 0)
                        output.WriteLine($"Activity on {i},{j} did not match first at these indexs: {string.Join(",", badIndexes.Select(idx => $"{idx}: {first[idx]}!={result[idx]}"))}");
                    j++;
                }
                j = 0;
                foreach (int[] result in actual)
                {
                    badIndexes = result.Select((v, idx) => v == expected[idx] ? -1 : idx).Where(v => v != -1).ToList();
                    if (badIndexes.Count != 0)
                        output.WriteLine($"Activity on {i},{j} did not match expected at these indexs: {string.Join(",", badIndexes.Select(idx => $"{idx}: {expected[idx]}!={result[idx]}"))}");
                    j++;
                }
                i++;
            }
        }

        public class SiloBuilderConfiguratorUsingAzureClustering : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.UseAzureStorageClustering(options =>
                    options.ConnectionString = TestDefaultConfiguration.DataConnectionString);
            }
        }

        public class ClientBuilderConfiguratorUsingAzureClustering : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.UseAzureStorageClustering(options =>
                    options.ConnectionString = TestDefaultConfiguration.DataConnectionString);
            }
        }
    }
}
