﻿using System;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Linq;
using Orleans;
using TestGrainInterfaces;
using Xunit;
using Assert = Xunit.Assert;
using TestExtensions;
using Xunit.Abstractions;
using Orleans.Runtime;
using System.Collections.Generic;
using Xunit.Sdk;

namespace Tester.EventSourcingTests
{
    [TestCaseOrderer("Tester.EventSourcingTests.SimplePriorityOrderer", "Tester")]
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

        private int iterations = 800;

        [Fact, RunThisFirst, TestCategory("EventSourcing")]
        public Task Perf_Warmup()
        {
            // call reset on each grain to ensure everything is loaded and primed
            return Task.WhenAll(
                GrainClient.GrainFactory.GetGrain<ICountersGrain>(0, "TestGrains.CountersGrain_StateStore_NonReentrant").Reset(true),
                GrainClient.GrainFactory.GetGrain<ICountersGrain>(0, "TestGrains.CountersGrain_StateStore_Reentrant").Reset(true),
                GrainClient.GrainFactory.GetGrain<ICountersGrain>(0, "TestGrains.CountersGrain_LogStore_NonReentrant").Reset(true),
                GrainClient.GrainFactory.GetGrain<ICountersGrain>(0, "TestGrains.CountersGrain_LogStore_Reentrant").Reset(true)
            );
        }

        [Fact, TestCategory("EventSourcing")]
        public async Task Perf_ConfirmEachUpdate_MemoryStateStore_NonReentrant()
        {
            var grain = GrainClient.GrainFactory.GetGrain<ICountersGrain>(0, "TestGrains.CountersGrain_StateStore_NonReentrant");
            await ConcurrentIncrements(grain, iterations, true);
        }
        [Fact, TestCategory("EventSourcing")]
        public async Task Perf_ConfirmAtEndOnly_MemoryStateStore_NonReentrant()
        {
            var grain = GrainClient.GrainFactory.GetGrain<ICountersGrain>(0, "TestGrains.CountersGrain_StateStore_NonReentrant");
            await ConcurrentIncrements(grain, iterations, false);
        }
        [Fact, TestCategory("EventSourcing")]
        public async Task Perf_ConfirmEachUpdate_MemoryLogStore_NonReentrant()
        {
            var grain = GrainClient.GrainFactory.GetGrain<ICountersGrain>(0, "TestGrains.CountersGrain_LogStore_NonReentrant");
            await ConcurrentIncrements(grain, iterations, true);
        }
        [Fact, TestCategory("EventSourcing")]
        public async Task Perf_ConfirmAtEndOnly_MemoryLogStore_NonReentrant()
        {
            var grain = GrainClient.GrainFactory.GetGrain<ICountersGrain>(0, "TestGrains.CountersGrain_LogStore_NonReentrant");
            await ConcurrentIncrements(grain, iterations, false);
        }
        [Fact, TestCategory("EventSourcing")]
        public async Task Perf_ConfirmEachUpdate_MemoryStateStore_Reentrant()
        {
            var grain = GrainClient.GrainFactory.GetGrain<ICountersGrain>(0, "TestGrains.CountersGrain_StateStore_Reentrant");
            await ConcurrentIncrements(grain, iterations, true);
        }
        [Fact, TestCategory("EventSourcing")]
        public async Task Perf_ConfirmAtEndOnly_MemoryStateStore_Reentrant()
        {
            var grain = GrainClient.GrainFactory.GetGrain<ICountersGrain>(0, "TestGrains.CountersGrain_StateStore_Reentrant");
            await ConcurrentIncrements(grain, iterations, false);
        }
        [Fact, TestCategory("EventSourcing")]
        public async Task Perf_ConfirmEachUpdate_MemoryLogStore_Reentrant()
        {
            var grain = GrainClient.GrainFactory.GetGrain<ICountersGrain>(0, "TestGrains.CountersGrain_LogStore_Reentrant");
            await ConcurrentIncrements(grain, iterations, true);
        }
        [Fact, TestCategory("EventSourcing")]
        public async Task Perf_ConfirmAtEndOnly_MemoryLogStore_Reentrant()
        {
            var grain = GrainClient.GrainFactory.GetGrain<ICountersGrain>(0, "TestGrains.CountersGrain_LogStore_Reentrant");
            await ConcurrentIncrements(grain, iterations, false);
        }


    }

    class RunThisFirstAttribute : Attribute
    {
    }

    public class SimplePriorityOrderer : ITestCaseOrderer
    {
        private string attrname = typeof(RunThisFirstAttribute).AssemblyQualifiedName;

        private bool HasRunThisFirstAttribute(ITestCase testcase)
        {
            return testcase.TestMethod.Method.GetCustomAttributes(attrname).Count() > 0;
        }

        public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases) where TTestCase : ITestCase
        {
            // return all tests with RunThisFirst attribute
            foreach (var tc in testCases.Where(tc => HasRunThisFirstAttribute(tc)))
                yield return tc;

            // return all other tests
            foreach (var tc in testCases.Where(tc => !HasRunThisFirstAttribute(tc)))
                yield return tc;
        }
    }

}