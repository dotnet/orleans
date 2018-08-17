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
using Orleans.Transactions.Tests.Grains;
using TestExtensions;

namespace Orleans.Transactions.Tests
{
    public class TransactionRecoveryTestsRunner : TransactionTestRunnerBase
    {
        private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(60);

        private readonly Random random;
        private readonly TestCluster testCluster;
        private readonly ILogger logger;

        protected void Log(string message)
        {
            this.output.WriteLine($"[{DateTime.Now}] {message}");
            this.logger.LogInformation(message);
        }

        private class ExpectedGrainActivity
        {
            public ITransactionalBitArrayGrain Grain { get; set; }
            public BitArrayState Expected { get; } = new BitArrayState();
            public BitArrayState Unambiguous { get; } = new BitArrayState();
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
            var index = new[] { 0 };
            Func<int> getIndex = () => index[0]++;
            List<ExpectedGrainActivity> txGrains = Enumerable.Range(0, grainCount)
                .Select(i => new ExpectedGrainActivity { Grain = RandomTestGrain<ITransactionalBitArrayGrain>(transactionTestGrainClassName) })
                .ToList();
            var txSucceedBeforeInterruption = await AllTxSucceed(txGrains, getIndex());
            Assert.True(txSucceedBeforeInterruption);
            await ValidateResults(txGrains);

            // have transactions in flight when silo goes down
            Task succeeding = RunWhileSucceeding(txGrains, getIndex);

            var siloToTerminate = this.testCluster.Silos[this.random.Next(this.testCluster.Silos.Count)];
            this.Log($"Warmup transaction succeeded. {(gracefulShutdown ? "Stopping" : "Killing")} silo {siloToTerminate.SiloAddress} ({siloToTerminate.Name}) and continuing");
            
            if (gracefulShutdown)
                this.testCluster.StopSilo(siloToTerminate);
            else
                this.testCluster.KillSilo(siloToTerminate);

            this.Log("Waiting for transactions to stop completing successfully");
            await succeeding;

            this.Log($"Waiting for system to recover. Performed {index[0]} transactions on each group.");
            await TestingUtils.WaitUntilAsync(lastTry => CheckTxResult(txGrains, getIndex, lastTry), RecoveryTimeout);
            this.Log($"Recovery completed. Performed {index[0]} transactions on each group. Validating results.");
            await ValidateResults(txGrains);
        }

        private async Task RunWhileSucceeding(IList<ExpectedGrainActivity> txGrains, Func<int> getIndex)
        {
            var startTime = DateTime.Now;
            while (await AllTxSucceed(txGrains, getIndex()) && DateTime.Now - startTime < TimeSpan.FromSeconds(10))
            {
                // Loop until failure.
            }
        }

        private async Task<bool> CheckTxResult(IList<ExpectedGrainActivity> txGrains, Func<int> getIndex, bool assertIsTrue)
        {
            var succeed = await AllTxSucceed(txGrains, getIndex());
            this.Log($"All transactions succeed after interruption : {succeed}");
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
                // Collect the indices of the pairs which failed their transactions for diagnostics.
                var failedGroups = tasks.Select((task, i) => new {task, i}).Where(t => t.task.IsFaulted).Select(t => t.i).ToList();
                this.Log($"Some transactions failed. Index: {index}. {failedGroups.Count} out of {tasks.Count} failed. Failed groups: {string.Join(", ", failedGroups)}");
                return false;
            }

            return true;
        }

        private async Task SetBit(List<ExpectedGrainActivity> grains, int index)
        {
            try
            {
                await this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid()).MultiGrainSetBit(grains.Select(v => v.Grain).ToList(), index);
                grains.ForEach(g =>
                {
                    g.Expected.Set(index, true);
                    g.Unambiguous.Set(index, true);
                });
            }
            catch (OrleansTransactionAbortedException e)
            {
                this.Log($"Some transactions failed. Index: {index}: Exception: {e.GetType().Name}");
                grains.ForEach(g =>
                {
                    g.Expected.Set(index, false);
                    g.Unambiguous.Set(index, true);
                });
                throw;
            }
            catch (Exception e)
            {
                this.Log($"Ambiguous transaction failure. Index: {index}: Exception: {e.GetType().Name}");
                grains.ForEach(g =>
                {
                    g.Expected.Set(index, false);
                    g.Unambiguous.Set(index, false);
                });
                throw;
            }
        }

        private async Task ValidateResults(List<ExpectedGrainActivity> txGrains)
        {
            await ReportResults(txGrains);
            foreach (ExpectedGrainActivity activity in txGrains)
            {
                var grain = activity.Grain;
                List<BitArrayState> actual = await grain.Get();
                // self consistency check, all resources should be the same
                BitArrayState first = actual.FirstOrDefault();
                Assert.NotNull(first);
                foreach (BitArrayState result in actual)
                {
                    Assert.Equal(first.Length, result.Length);
                    Assert.Equal(first.ToString(), result.ToString());
                }

                // Check against expected.
                // Only need to check if behavior was not ambiguous.
                // Only need to check first, since we've already verified all resources are the same.
                var expected = activity.Expected;
                var unambigous = activity.Unambiguous;
                Assert.Equal(first.Length, expected.Length);

                var unambiguousExpected = expected & unambigous;
                var unambiguousFirst = first & unambigous;
                var difference = unambiguousFirst ^ unambiguousExpected;

                if (unambiguousExpected != unambiguousFirst)
                {
                    this.Log("Inconsistent results:\n" +
                             $"{first} Actual state\n" +
                             $"{expected} Expected state\n" +
                             $"{unambigous} Unambiguous bits\n" +
                             $"{difference} Inconsistencies\n" +
                             $"Grain: {grain}");
                }

                Assert.Equal(unambiguousExpected.ToString(), unambiguousFirst.ToString());
            }
        }

        private async Task ReportResults(List<ExpectedGrainActivity> txGrains)
        {
            int i = 0;
            foreach (ExpectedGrainActivity activity in txGrains)
            {
                var expected = activity.Expected;
                var unambiguous = activity.Unambiguous;
                var grain = activity.Grain;
                var actual = await grain.Get();
                var first = actual.FirstOrDefault();
                if(first == null)
                {
                    this.Log($"No activity for {i} ({grain})");
                    return;
                }

                int j = 0;
                foreach (var result in actual)
                {
                    // Check if each state is identical to the first state.
                    var difference = result ^ first;
                    if (difference.Value.Any(v => v != 0))
                    {
                        this.Log($"Activity on grain {i}, state {j} did not match 'first':\n"
                                 + $"  {first}\n"
                                 + $"^ {result}\n"
                                 + $"= {difference}\n"
                                 + $"Activation: {grain}");
                    }

                    j++;
                }

                j = 0;
                foreach (var result in actual)
                {
                    // Check if the unambiguous portions of the results match.
                    var unambiguousResult = result & unambiguous;
                    var unambuguousExpected = expected & unambiguous;
                    var difference = result ^ first;

                    if (difference.Value.Any(v => v != 0))
                    {
                        this.Log(
                            $"Activity on grain {i}, state {j} did not match 'expected':\n"
                            + $"  {unambuguousExpected}\n"
                            + $"^ {unambiguousResult}\n"
                            + $"= {difference}\n"
                            + $"Activation: {grain}");
                    }

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
