using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using Orleans.Transactions.TestKit.Correctnesss;

namespace Orleans.Transactions.TestKit
{
    public class TransactionRecoveryTestsRunner : TransactionTestRunnerBase
    {
        private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(60);
        // reduce to or remove once we fix timeouts abort
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

        private readonly TestCluster testCluster;
        private readonly ILogger logger;

        protected void Log(string message)
        {
            this.testOutput($"[{DateTime.Now}] {message}");
            this.logger.LogInformation(message);
        }

        private class ExpectedGrainActivity
        {
            public ExpectedGrainActivity(Guid grainId, ITransactionalBitArrayGrain grain)
            {
                this.GrainId = grainId;
                this.Grain = grain;
            }
            public Guid GrainId { get; }
            public ITransactionalBitArrayGrain Grain { get; }
            public BitArrayState Expected { get; } = new BitArrayState();
            public BitArrayState Unambiguous { get; } = new BitArrayState();
            public List<BitArrayState> Actual { get; set; }
            public async Task GetActual()
            {
                try
                {
                    this.Actual = await this.Grain.Get();
                } catch(Exception)
                {
                    // allow a single retry
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    this.Actual = await this.Grain.Get();
                }
            }
        }

        public TransactionRecoveryTestsRunner(TestCluster testCluster, Action<string> testOutput)
            : base(testCluster.GrainFactory, testOutput)
        {
            this.testCluster = testCluster;
            this.logger = this.testCluster.ServiceProvider.GetService<ILogger<TransactionRecoveryTestsRunner>>();
        }

        public virtual Task TransactionWillRecoverAfterRandomSiloGracefulShutdown(string transactionTestGrainClassName, int concurrent)
        {
            return TransactionWillRecoverAfterRandomSiloFailure(transactionTestGrainClassName, concurrent, true);
        }

        public virtual Task TransactionWillRecoverAfterRandomSiloUnGracefulShutdown(string transactionTestGrainClassName, int concurrent)
        {
            return TransactionWillRecoverAfterRandomSiloFailure(transactionTestGrainClassName, concurrent, false);
        }

        protected virtual async Task TransactionWillRecoverAfterRandomSiloFailure(string transactionTestGrainClassName, int concurrent, bool gracefulShutdown)
        {
            var endOnCommand = new[] { false };
            var index = new[] { 0 };
            int getIndex() => index[0]++;
            List<ExpectedGrainActivity> txGrains = Enumerable.Range(0, concurrent * 2)
                .Select(i => Guid.NewGuid())
                .Select(grainId => new ExpectedGrainActivity(grainId, TestGrain<ITransactionalBitArrayGrain>(transactionTestGrainClassName, grainId)))
                .ToList();
            //ping all grains to activate them
            await WakeupGrains(txGrains.Select(g=>g.Grain).ToList());
            List<ExpectedGrainActivity>[] transactionGroups = txGrains
                .Select((txGrain, i) => new { index = i, value = txGrain })
                .GroupBy(v => v.index / 2)
                .Select(g => g.Select(i => i.value).ToList())
                .ToArray();
            var txSucceedBeforeInterruption = await AllTxSucceed(transactionGroups, getIndex());
            txSucceedBeforeInterruption.Should().BeTrue();
            await ValidateResults(txGrains, transactionGroups);

            // have transactions in flight when silo goes down
            Task<bool> succeeding = RunWhileSucceeding(transactionGroups, getIndex, endOnCommand);
            await Task.Delay(TimeSpan.FromSeconds(2));

            var siloToTerminate = this.testCluster.Silos[Random.Shared.Next(this.testCluster.Silos.Count)];
            this.Log($"Warmup transaction succeeded. {(gracefulShutdown ? "Stopping" : "Killing")} silo {siloToTerminate.SiloAddress} ({siloToTerminate.Name}) and continuing");

            if (gracefulShutdown)
                await this.testCluster.StopSiloAsync(siloToTerminate);
            else
                await this.testCluster.KillSiloAsync(siloToTerminate);

            this.Log("Waiting for transactions to stop completing successfully");
            var complete = await Task.WhenAny(succeeding, Task.Delay(TimeSpan.FromSeconds(30)));
            endOnCommand[0] = true;
            bool endedOnCommand = await succeeding;
            if (endedOnCommand) this.Log($"No transactions failed due to silo death.  Test may not be valid");

            this.Log($"Waiting for system to recover. Performed {index[0]} transactions on each group.");
            var transactionGroupsRef = new[] { transactionGroups };
            await TestingUtils.WaitUntilAsync(lastTry => CheckTxResult(transactionGroupsRef, getIndex, lastTry), RecoveryTimeout, RetryDelay);
            this.Log($"Recovery completed. Performed {index[0]} transactions on each group. Validating results.");
            await ValidateResults(txGrains, transactionGroups);
        }

        private Task WakeupGrains(List<ITransactionalBitArrayGrain> grains)
        {
            var tasks =  new List<Task>();
            foreach (var grain in grains)
            {
                tasks.Add(grain.Ping());
            }
            return Task.WhenAll(tasks);
        }

        private async Task<bool> RunWhileSucceeding(List<ExpectedGrainActivity>[] transactionGroups, Func<int> getIndex, bool[] end)
        {
            // Loop until failure, or getTime changes
            while (await AllTxSucceed(transactionGroups, getIndex()) && !end[0])
            {
            }
            return end[0];
        }

        private async Task<bool> CheckTxResult(List<ExpectedGrainActivity>[][] transactionGroupsRef, Func<int> getIndex, bool assertIsTrue)
        {
            // only retry failed transactions
            transactionGroupsRef[0] = await RunAllTxReportFailed(transactionGroupsRef[0], getIndex());
            bool succeed = transactionGroupsRef[0] == null;
            this.Log($"All transactions succeed after interruption : {succeed}");
            if (assertIsTrue)
            {
                //consider it recovered if all tx succeed
                this.Log($"Final check : {succeed}");
                succeed.Should().BeTrue();
                return succeed;
            }
            else
            {
                return succeed;
            }
        }

        // Runs all transactions and returns failed;
        private async Task<List<ExpectedGrainActivity>[]> RunAllTxReportFailed(List<ExpectedGrainActivity>[] transactionGroups, int index)
        {
            List<Task> tasks = transactionGroups
                .Select(p => SetBit(p, index))
                .ToList();
            try
            {
                await Task.WhenAll(tasks);
                return null;
            }
            catch (Exception)
            {
                // Collect the indices of the transaction groups which failed their transactions for diagnostics.
                List<ExpectedGrainActivity>[] failedGroups = tasks.Select((task, i) => new { task, i }).Where(t => t.task.IsFaulted).Select(t => transactionGroups[t.i]).ToArray();
                this.Log($"Some transactions failed. Index: {index}. {failedGroups.Length} out of {tasks.Count} failed. Failed groups: {string.Join(", ", failedGroups.Select(transactionGroup => string.Join(":", transactionGroup.Select(a => a.GrainId))))}");
                return failedGroups;
            }
        }

        private async Task<bool> AllTxSucceed(List<ExpectedGrainActivity>[] transactionGroups, int index)
        {
            // null return indicates none failed
            return (await RunAllTxReportFailed(transactionGroups, index) == null);
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

        private async Task ValidateResults(List<ExpectedGrainActivity> txGrains, List<ExpectedGrainActivity>[] transactionGroups)
        {
            await Task.WhenAll(txGrains.Select(a => a.GetActual()));
            this.Log($"Got all {txGrains.Count} actual values");

            bool pass = true;
            foreach (List<ExpectedGrainActivity> transactionGroup in transactionGroups)
            {
                if (transactionGroup.Count == 0) continue;
                BitArrayState first = transactionGroup[0].Actual.FirstOrDefault();
                foreach (ExpectedGrainActivity activity in transactionGroup.Skip(1))
                {
                    BitArrayState actual = activity.Actual.FirstOrDefault();
                    BitArrayState difference = first ^ actual;
                    if (difference.Value.Any(v => v != 0))
                    {
                        this.Log($"Activity on grain {activity.GrainId} did not match activity on {transactionGroup[0].GrainId}:\n"
                                 + $"{first} ^\n"
                                 + $"{actual} = \n"
                                 + $"{difference}\n"
                                 + $"Activation: {activity.GrainId}");
                        pass = false;
                    }

                }
            }

            int i = 0;
            foreach (ExpectedGrainActivity activity in txGrains)
            {
                BitArrayState expected = activity.Expected;
                BitArrayState unambiguous = activity.Unambiguous;
                BitArrayState unambuguousExpected = expected & unambiguous;
                List<BitArrayState> actual = activity.Actual;
                BitArrayState first = actual.FirstOrDefault();
                if (first == null)
                {
                    this.Log($"No activity for {i} ({activity.GrainId})");
                    pass = false;
                    continue;
                }

                int j = 0;
                foreach (BitArrayState result in actual)
                {
                    // skip comparing first to first.
                    if (ReferenceEquals(first, result)) continue;
                    // Check if each state is identical to the first state.
                    var difference = result ^ first;
                    if (difference.Value.Any(v => v != 0))
                    {
                        this.Log($"Activity on grain {i}, state {j} did not match 'first':\n"
                                 + $"  {first}\n"
                                 + $"^ {result}\n"
                                 + $"= {difference}\n"
                                 + $"Activation: {activity.GrainId}");
                        pass = false;
                    }

                    j++;
                }

                // Check if the unambiguous portions of the first match.
                var unambiguousFirst = first & unambiguous;
                var unambiguousDifference = unambuguousExpected ^ unambiguousFirst;

                if (unambiguousDifference.Value.Any(v => v != 0))
                {
                    this.Log(
                        $"First state on grain {i} did not match 'expected':\n"
                        + $"  {unambuguousExpected}\n"
                        + $"^ {unambiguousFirst}\n"
                        + $"= {unambiguousDifference}\n"
                        + $"Activation: {activity.GrainId}");
                    pass = false;
                }

                i++;
            }
            this.Log($"Report complete : {pass}");
            pass.Should().BeTrue();
        }
    }
}
