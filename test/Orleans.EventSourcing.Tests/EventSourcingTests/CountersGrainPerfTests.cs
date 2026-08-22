using TestGrainInterfaces;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace Tester.EventSourcingTests
{
    /// <summary>
    /// Performance tests for event-sourced counters grain comparing different synchronization and reentrancy strategies.
    /// </summary>
    [TestCaseOrderer(typeof(SimplePriorityOrderer))]
    public partial class CountersGrainTests
    {

        // you can look at the time taken by each of the tests below
        // to get a rough idea on how the synchronization choices, and the configuration parameters,
        // and the consistency provider, affect throughput

        // To run these perf tests from within visual studio, first type
        // "CountersGrainTests.Perf" in the search box, and then "Run All"
        // This will run the warmup and then all tests, in the same test cluster. Afterwards it reports
        // approximate time taken for each. It's not really a test, just an
        // illustration of how JournaledGrain performance can vary with the choices made.

        // what you should see is:
        // - the conservative approach (confirm each update, disallow reentrancy) is slow.
        // - confirming at end only, instead of after each update, is fast.
        // - allowing reentrancy, while still confirming after each update, is also fast.

        private const int iterations = 400;

        [TestArea("EventSourcing")]
        [Fact, RunThisFirst, TestSuite("Nightly"), TestCategory("EventSourcing")]
        public Task Perf_Warmup()
        {
            // Call reset on each grain to ensure every test activation is loaded and primed.
            return Task.WhenAll(
                GetGrain(PerfGrain.MemoryStateStoreNonReentrantConfirmEach, "TestGrains.CountersGrain_StateStore_NonReentrant").Reset(true),
                GetGrain(PerfGrain.MemoryStateStoreNonReentrantConfirmAtEnd, "TestGrains.CountersGrain_StateStore_NonReentrant").Reset(true),
                GetGrain(PerfGrain.MemoryLogStoreNonReentrantConfirmEach, "TestGrains.CountersGrain_LogStore_NonReentrant").Reset(true),
                GetGrain(PerfGrain.MemoryLogStoreNonReentrantConfirmAtEnd, "TestGrains.CountersGrain_LogStore_NonReentrant").Reset(true),
                GetGrain(PerfGrain.MemoryStateStoreReentrantConfirmEach, "TestGrains.CountersGrain_StateStore_Reentrant").Reset(true),
                GetGrain(PerfGrain.MemoryStateStoreReentrantConfirmAtEnd, "TestGrains.CountersGrain_StateStore_Reentrant").Reset(true),
                GetGrain(PerfGrain.MemoryLogStoreReentrantConfirmEach, "TestGrains.CountersGrain_LogStore_Reentrant").Reset(true),
                GetGrain(PerfGrain.MemoryLogStoreReentrantConfirmAtEnd, "TestGrains.CountersGrain_LogStore_Reentrant").Reset(true)
            );
        }

        [TestArea("EventSourcing")]
        [Fact, TestSuite("Nightly"), TestCategory("EventSourcing")]
        public async Task Perf_ConfirmEachUpdate_MemoryStateStore_NonReentrant()
        {
            var grain = GetGrain(PerfGrain.MemoryStateStoreNonReentrantConfirmEach, "TestGrains.CountersGrain_StateStore_NonReentrant");
            await ConcurrentIncrementsRunner(grain, iterations, true);
        }
        [TestArea("EventSourcing")]
        [Fact, TestSuite("Nightly"), TestCategory("EventSourcing")]
        public async Task Perf_ConfirmAtEndOnly_MemoryStateStore_NonReentrant()
        {
            var grain = GetGrain(PerfGrain.MemoryStateStoreNonReentrantConfirmAtEnd, "TestGrains.CountersGrain_StateStore_NonReentrant");
            await ConcurrentIncrementsRunner(grain, iterations, false);
        }
        [TestArea("EventSourcing")]
        [Fact, TestSuite("Nightly"), TestCategory("EventSourcing")]
        public async Task Perf_ConfirmEachUpdate_MemoryLogStore_NonReentrant()
        {
            var grain = GetGrain(PerfGrain.MemoryLogStoreNonReentrantConfirmEach, "TestGrains.CountersGrain_LogStore_NonReentrant");
            await ConcurrentIncrementsRunner(grain, iterations, true);
        }
        [TestArea("EventSourcing")]
        [Fact, TestSuite("Nightly"), TestCategory("EventSourcing")]
        public async Task Perf_ConfirmAtEndOnly_MemoryLogStore_NonReentrant()
        {
            var grain = GetGrain(PerfGrain.MemoryLogStoreNonReentrantConfirmAtEnd, "TestGrains.CountersGrain_LogStore_NonReentrant");
            await ConcurrentIncrementsRunner(grain, iterations, false);
        }
        [TestArea("EventSourcing")]
        [Fact, TestSuite("Nightly"), TestCategory("EventSourcing")]
        public async Task Perf_ConfirmEachUpdate_MemoryStateStore_Reentrant()
        {
            var grain = GetGrain(PerfGrain.MemoryStateStoreReentrantConfirmEach, "TestGrains.CountersGrain_StateStore_Reentrant");
            await ConcurrentIncrementsRunner(grain, iterations, true);
        }
        [TestArea("EventSourcing")]
        [Fact, TestSuite("Nightly"), TestCategory("EventSourcing")]
        public async Task Perf_ConfirmAtEndOnly_MemoryStateStore_Reentrant()
        {
            var grain = GetGrain(PerfGrain.MemoryStateStoreReentrantConfirmAtEnd, "TestGrains.CountersGrain_StateStore_Reentrant");
            await ConcurrentIncrementsRunner(grain, iterations, false);
        }
        [TestArea("EventSourcing")]
        [Fact, TestSuite("Nightly"), TestCategory("EventSourcing")]
        public async Task Perf_ConfirmEachUpdate_MemoryLogStore_Reentrant()
        {
            var grain = GetGrain(PerfGrain.MemoryLogStoreReentrantConfirmEach, "TestGrains.CountersGrain_LogStore_Reentrant");
            await ConcurrentIncrementsRunner(grain, iterations, true);
        }
        [TestArea("EventSourcing")]
        [Fact, TestSuite("Nightly"), TestCategory("EventSourcing")]
        public async Task Perf_ConfirmAtEndOnly_MemoryLogStore_Reentrant()
        {
            var grain = GetGrain(PerfGrain.MemoryLogStoreReentrantConfirmAtEnd, "TestGrains.CountersGrain_LogStore_Reentrant");
            await ConcurrentIncrementsRunner(grain, iterations, false);
        }

        private ICountersGrain GetGrain(PerfGrain grain, string grainClassName) =>
            this.fixture.GrainFactory.GetGrain<ICountersGrain>((long)grain, grainClassName);

        private enum PerfGrain
        {
            MemoryStateStoreNonReentrantConfirmEach,
            MemoryStateStoreNonReentrantConfirmAtEnd,
            MemoryLogStoreNonReentrantConfirmEach,
            MemoryLogStoreNonReentrantConfirmAtEnd,
            MemoryStateStoreReentrantConfirmEach,
            MemoryStateStoreReentrantConfirmAtEnd,
            MemoryLogStoreReentrantConfirmEach,
            MemoryLogStoreReentrantConfirmAtEnd,
        }
    }

    internal class RunThisFirstAttribute : Attribute
    {
    }

    public class SimplePriorityOrderer : ITestCaseOrderer
    {
        private readonly string attrname = typeof(RunThisFirstAttribute).AssemblyQualifiedName!;

        private static bool HasRunThisFirstAttribute(ITestCase testCase)
        {
            return testCase is IXunitTestCase xunitTestCase
                && xunitTestCase.TestMethod.Method.GetCustomAttributes(typeof(RunThisFirstAttribute), inherit: true).Any();
        }

        public IReadOnlyCollection<TTestCase> OrderTestCases<TTestCase>(IReadOnlyCollection<TTestCase> testCases) where TTestCase : ITestCase
        {
            return
            [
                .. testCases.Where(testCase => HasRunThisFirstAttribute(testCase)),
                .. testCases.Where(testCase => !HasRunThisFirstAttribute(testCase)),
            ];
        }
    }

}
