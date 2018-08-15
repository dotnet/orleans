using Orleans.TestingHost;
using Orleans.Transactions.Tests;
using Orleans.Transactions.Tests.Consistency;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.AzureStorage.Tests
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class FaultInjectionConsistencyTests: TransactionTestRunnerBase, IClassFixture<FaultInjectedTestFixture>
    {
        public FaultInjectionConsistencyTests(FaultInjectedTestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        { }

        [SkippableTheory]
        // high congestion
        [InlineData(2, 2, true, true, ReadWriteDetermination.PerGrain)]
        [InlineData(2, 3, true, true, ReadWriteDetermination.PerGrain)]
        [InlineData(2, 4, true, true, ReadWriteDetermination.PerGrain)]
        [InlineData(2, 5, true, true, ReadWriteDetermination.PerGrain)]
        [InlineData(2, 2, true, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(2, 3, true, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(2, 4, true, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(2, 5, true, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(2, 2, true, true, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 3, true, true, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 4, true, true, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 5, true, true, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 2, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 3, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 4, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 5, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 2, true, false, ReadWriteDetermination.PerGrain)]
        [InlineData(2, 3, true, false, ReadWriteDetermination.PerGrain)]
        [InlineData(2, 4, true, false, ReadWriteDetermination.PerGrain)]
        [InlineData(2, 5, true, false, ReadWriteDetermination.PerGrain)]
        [InlineData(2, 2, true, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(2, 3, true, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(2, 4, true, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(2, 5, true, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(2, 2, true, false, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 3, true, false, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 4, true, false, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 5, true, false, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 2, false, false, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 3, false, false, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 4, false, false, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 5, false, false, ReadWriteDetermination.PerAccess)]
        // medium congestion
        [InlineData(30, 2, true, true, ReadWriteDetermination.PerGrain)]
        [InlineData(30, 3, true, true, ReadWriteDetermination.PerGrain)]
        [InlineData(30, 4, true, true, ReadWriteDetermination.PerGrain)]
        [InlineData(30, 2, true, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(30, 3, true, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(30, 4, true, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(30, 2, true, true, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 3, true, true, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 4, true, true, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 2, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 3, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 4, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 2, true, false, ReadWriteDetermination.PerGrain)]
        [InlineData(30, 3, true, false, ReadWriteDetermination.PerGrain)]
        [InlineData(30, 4, true, false, ReadWriteDetermination.PerGrain)]
        [InlineData(30, 5, true, false, ReadWriteDetermination.PerGrain)]
        [InlineData(30, 2, true, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(30, 3, true, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(30, 4, true, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(30, 5, true, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(30, 2, true, false, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 3, true, false, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 4, true, false, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 5, true, false, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 2, false, false, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 3, false, false, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 4, false, false, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 5, false, false, ReadWriteDetermination.PerAccess)]
        // low congestion
        [InlineData(1000, 2, false, true, ReadWriteDetermination.PerGrain)]
        [InlineData(1000, 3, false, true, ReadWriteDetermination.PerGrain)]
        [InlineData(1000, 4, false, true, ReadWriteDetermination.PerGrain)]
        [InlineData(1000, 2, false, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(1000, 3, false, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(1000, 4, false, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(1000, 2, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(1000, 3, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(1000, 4, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(1000, 2, false, false, ReadWriteDetermination.PerGrain)]
        [InlineData(1000, 3, false, false, ReadWriteDetermination.PerGrain)]
        [InlineData(1000, 4, false, false, ReadWriteDetermination.PerGrain)]
        [InlineData(1000, 5, false, false, ReadWriteDetermination.PerGrain)]
        [InlineData(1000, 2, false, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(1000, 3, false, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(1000, 4, false, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(1000, 5, false, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(1000, 2, false, false, ReadWriteDetermination.PerAccess)]
        [InlineData(1000, 3, false, false, ReadWriteDetermination.PerAccess)]
        [InlineData(1000, 4, false, false, ReadWriteDetermination.PerAccess)]
        [InlineData(1000, 5, false, false, ReadWriteDetermination.PerAccess)]

        public virtual async Task RandomizedConsistency(int numGrains, int scale, bool avoidDeadlocks, bool avoidTimeouts, ReadWriteDetermination readwrite)
        {
            var random = new Random(scale + numGrains * 1000 + (avoidDeadlocks ? 666 : 333) + ((int)readwrite) * 123976);

            var harness = new ConsistencyTestHarness(grainFactory, numGrains, random.Next(), avoidDeadlocks, avoidTimeouts, readwrite, true);

            // first, run the random work load to generate history events
            output.WriteLine($"start at {DateTime.UtcNow}");
            int numThreads = scale;
            int numTxsPerThread = scale * scale;

            // start the threads that run transactions
            var tasks = new Task[numThreads];
            for (int i = 0; i < numThreads; i++)
            {
                tasks[i] = harness.RunRandomTransactionSequence(i, numTxsPerThread, grainFactory, this.output);
            }

            // wait for the test to finish
            await Task.WhenAll(tasks);
            output.WriteLine($"end at {DateTime.UtcNow}");

            // then, analyze the history results
            harness.CheckConsistency(tolerateGenericTimeouts: true, tolerateUnknownExceptions: true);
        }  
    }
}
