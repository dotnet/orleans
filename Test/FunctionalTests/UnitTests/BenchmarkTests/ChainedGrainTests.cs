using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using BenchmarkGrains;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;

namespace UnitTests.BenchmarkTests
{

#if DEBUG || REVISIT

    /// <summary>
    /// Summary description for BenchmarkTests
    /// </summary>
    [TestClass]
    public class ChainedGrainTests : UnitTestBase
    {
        private static readonly TimeSpan timeout = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(1);

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        [ClassInitialize]
        public static void MyClassInitialize(TestContext testContext)
        {
            ResetDefaultRuntimes();
        }

        [TestMethod, TestCategory("Failures"), TestCategory("Stress")]
        public void ChainedGrain_10RowsOf5()
        {
            IChainedGrain[,] grains = CreateGrainMatrix(5, 10);
            Test(10, grains);
        }

        [TestMethod, TestCategory("Failures"), TestCategory("Stress")]
        public void ChainedGrain_100RowsOf10()
        {
            IChainedGrain[,] grains = CreateGrainMatrix(10, 100);
            Test(100, grains); 
        }

        [TestMethod, TestCategory("Failures"), TestCategory("ReadOnly"), TestCategory("Stress")]
        public void ChainedGrain_PerformanceDegradation()
        {
            int nRows = 20;

            IChainedGrain[,] grains = CreateGrainMatrix(10, nRows);

            Test(nRows, grains); // warm things up

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            Test(nRows, grains); // clock the second round
            stopwatch.Stop();
            long secondRoundElapsed = stopwatch.ElapsedMilliseconds;

            for(int i=0; i<7; i++)
                Test(nRows, grains); // run more tests 3-9

            stopwatch.Restart();
            Test(nRows, grains); // clock the 10th round
            stopwatch.Stop();
            long tenthRoundElapsed = stopwatch.ElapsedMilliseconds;

            if (tenthRoundElapsed > secondRoundElapsed)
                Assert.IsTrue(((double)tenthRoundElapsed - secondRoundElapsed) / secondRoundElapsed < 0.2, 
                    String.Format("Performance degradation exceeded the threshold of 20%. 2nd round: {0}, 10th round: {1}", secondRoundElapsed, tenthRoundElapsed));

        }

        private static IChainedGrain[,] CreateGrainMatrix(int callChainLength, int nRows)
        {
            Console.WriteLine("BenchmarkTest CreateGrain calls started");

            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();

            IChainedGrain[,] grains = new IChainedGrain[nRows, callChainLength];
            List<IAddressable> allGrains = new List<IAddressable>();
            List<Task> continuations = new List<Task>();
            
            for (int i = 0; i < nRows; i++)
            {
                for (int j = callChainLength - 1; j >= 0; j--)
                {
                    int id = i * callChainLength + j + 1;
                    IChainedGrain grain = ChainedGrainFactory.GetGrain(id);
                    grains[i, j] = grain;
                    allGrains.Add(grain);
                }
            }
 
            Console.WriteLine("BenchmarkTest CreateGrain calls for {0}x{1} matrix took: {2}", callChainLength, nRows, stopWatch.Elapsed);
            Console.WriteLine("BenchmarkTest grain creation for {0}x{1} matrix took: {2}", callChainLength, nRows, stopWatch.Elapsed);

            for (int i = 0; i < nRows; i++)
            {
                for (int j=0; j< callChainLength-1; j++)
                {
                    IChainedGrain grain = grains[i, j];
                    IChainedGrain next = grains[i, j + 1];
                    Task setNext = grain.SetNext(next);
                    continuations.Add(setNext);
                }
            }
            if(!Task.WhenAll(continuations).Wait(timeout))
                throw new TimeoutException();

            stopWatch.Stop();
            Console.WriteLine("BenchmarkTest setup for {0}x{1} matrix took: {2}", callChainLength, nRows, stopWatch.Elapsed);

            stopWatch.Restart();
            Console.WriteLine("Performing validation of created grain.");
            continuations.Clear();
            for (int i = 0; i < nRows; i++)
            {
                for (int j = 0; j < callChainLength; j++)
                {
                    IChainedGrain grain = grains[i, j];
                    Task validation = grain.Validate(j < callChainLength - 1);
                    continuations.Add(validation);
                }
            }
            if(!Task.WhenAll(continuations).Wait(timeout))
                throw new TimeoutException();

            stopWatch.Stop();
            Console.WriteLine("Validation completed in {0}", stopWatch.Elapsed);

            return grains;
        }

        private static void Test(int nRows, IChainedGrain[,] grains)
        {
            List<Task> promises = new List<Task>();
            Stopwatch[] stopwatches = new Stopwatch[nRows];
            
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();
            
            for (int i = 0; i < nRows; i++)
            {
                int row = i;
                int length = grains.Length / nRows;
                stopwatches[row] = new Stopwatch();
                stopwatches[row].Start();
                Task<int> promise = grains[i, 0].GetCalculatedValue();
                promises.Add(promise.ContinueWith((Task<int> x) =>
                    {
                        if (x.IsCompleted)
                        {
                            stopwatches[row].Stop();
                            int expectedValue = row*length + (1 + length)*length/2;
                            Assert.AreEqual<int>(expectedValue, x.Result);
                            if (expectedValue != x.Result)
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("Row {0}: {1}: {2}ms Expected:{3}", row, x,
                                                  stopwatches[row].ElapsedMilliseconds, expectedValue);
                                Console.ResetColor();
                            }
                        }
                        else
                        {
                            stopwatches[row].Stop();
                            Console.WriteLine("Row {0}: {1}: {2}ms", row, x.Exception.GetBaseException().Message,
                                              stopwatches[row].ElapsedMilliseconds);
                        }
                    }));
            }
            Task.WhenAll(promises).Wait();
            stopWatch.Stop();
            Console.WriteLine("BenchmarkTest GetCalculatedValue() calls for {0} rows took: {1}", nRows, stopWatch.Elapsed);
        }
    }
#endif
}
