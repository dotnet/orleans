﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests
{
    /// <summary>
    /// Summary description for ErrorHandlingGrainTest
    /// </summary>
    public class ErrorGrainTest : HostedTestClusterEnsureDefaultStarted
    {
        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(10);
        private readonly Logger Logger = LogManager.GetLogger("AssemblyLoaderTests", Orleans.Runtime.LoggerType.Application);
        private readonly ITestOutputHelper output;

        public ErrorGrainTest(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact, TestCategory("Functional"), TestCategory("ErrorHandling")]
        public async Task ErrorGrain_GetGrain()
        {
            var grainFullName = typeof(ErrorGrain).FullName;
            IErrorGrain grain = GrainClient.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainFullName);
            int ignored = await grain.GetA();
        }

        [Fact, TestCategory("Functional"), TestCategory("ErrorHandling")]
        public async Task ErrorHandlingLocalError()
        {
            LocalErrorGrain localGrain = new LocalErrorGrain();
            
            Task<int> intPromise = localGrain.GetAxBError();
            try
            {
                await intPromise;
                Assert.True(false, "Should not have executed");
            }
            catch (Exception exc2)
            {
                Assert.Equal(exc2.GetBaseException().Message, (new Exception("GetAxBError-Exception")).Message);
            }

            Assert.True(intPromise.Status == TaskStatus.Faulted);                
        }

        [Fact, TestCategory("Functional"), TestCategory("ErrorHandling")]
        // check that grain that throws an error breaks its promise and later Wait and GetValue on it will throw
        public void ErrorHandlingGrainError1()
        {
            var grainFullName = typeof(ErrorGrain).FullName;
            IErrorGrain grain = GrainClient.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainFullName);

            Task<int> intPromise = grain.GetAxBError();
            try
            {
                intPromise.Wait();
                Assert.True(false, "Should have thrown");
            }
            catch (Exception)
            {
                Assert.True(intPromise.Status == TaskStatus.Faulted);
            }

            try
            {
                intPromise.Wait();
                Assert.True(false, "Should have thrown");
            }
            catch (Exception exc2)
            {
                Assert.True(intPromise.Status == TaskStatus.Faulted);
                Assert.Equal((new Exception("GetAxBError-Exception")).Message, exc2.GetBaseException().Message);
            }

            Assert.True(intPromise.Status == TaskStatus.Faulted);
        }


        [Fact, TestCategory("Functional"), TestCategory("ErrorHandling")]
        // check that premature wait finishes on time with false.
        public void ErrorHandlingTimedMethod()
        {
            var grainFullName = typeof(ErrorGrain).FullName;
            IErrorGrain grain = GrainClient.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainFullName);

            Task promise = grain.LongMethod(2000);

            // there is a race in the test here. If run in debugger, the invocation can actually finish OK
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            bool finished = promise.Wait(TimeSpan.FromMilliseconds(1000));
            stopwatch.Stop();

            // these asserts depend on timing issues and will be wrong for the sync version of OrleansTask
            Assert.True(!finished);
            Assert.True(stopwatch.ElapsedMilliseconds >= 900, "Waited less than 900ms"); // check that we waited at least 0.9 second
            Assert.True(stopwatch.ElapsedMilliseconds <= 1100, "Waited longer than 1100ms");

            promise.Wait(); // just wait for the server side grain invocation to finish
            
            Assert.True(promise.Status == TaskStatus.RanToCompletion);
        }

        [Fact, TestCategory("Functional"), TestCategory("ErrorHandling")]
        // check that premature wait finishes on time but does not throw with false and later wait throws.
        public void ErrorHandlingTimedMethodWithError()
        {
            var grainFullName = typeof(ErrorGrain).FullName;
            IErrorGrain grain = GrainClient.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainFullName);

            Task promise = grain.LongMethodWithError(2000);

            // there is a race in the test here. If run in debugger, the invocation can actually finish OK
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            Assert.False(promise.Wait(1000), "The task shouldn't have completed yet.");

            stopwatch.Stop();
            Assert.True(stopwatch.ElapsedMilliseconds >= 900, "Waited less than 900ms"); // check that we waited at least 0.9 second
            Assert.True(stopwatch.ElapsedMilliseconds <= 1100, "Waited longer than 1100ms");

            try
            {
                promise.Wait();
                Assert.True(false, "Should have thrown");
            }
            catch (Exception)
            {
            }

            Assert.True(promise.Status == TaskStatus.Faulted);
        }

        [Fact, TestCategory("Functional"), TestCategory("ErrorHandling"), TestCategory("Stress")]
        public void StressHandlingMultipleDelayedRequests()
        {
            IErrorGrain grain = GrainClient.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId());
            bool once = true;
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < 500; i++)
            {
                Task promise = grain.DelayMethod(1);
                tasks.Add(promise);
                if (once)
                {
                    once = false;
                    promise.Wait();
                }

            }
            Task.WhenAll(tasks).Wait();
            Logger.Info(1, "DONE.");
        }

        [Fact, TestCategory("Functional"), TestCategory("ErrorHandling"), TestCategory("GrainReference")]
        public void ArgumentTypes_ListOfGrainReferences()
        {
            var grainFullName = typeof(ErrorGrain).FullName;
            List<IErrorGrain> list = new List<IErrorGrain>();
            IErrorGrain grain = GrainClient.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainFullName);
            list.Add(GrainClient.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainFullName));
            list.Add(GrainClient.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainFullName));
            bool ok = grain.AddChildren(list).Wait(timeout);
            if (!ok) throw new TimeoutException();
        }

        [Fact, TestCategory("Functional"), TestCategory("AsynchronyPrimitives"), TestCategory("ErrorHandling")]
        public async Task AC_DelayedExecutor_2()
        {
            var grainFullName = typeof(ErrorGrain).FullName;
            IErrorGrain grain = GrainClient.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainFullName);
            Task<bool> promise = grain.ExecuteDelayed(TimeSpan.FromMilliseconds(2000));
            bool result = await promise;
            Assert.Equal(true, result);
        }

        [Fact, TestCategory("Functional"), TestCategory("SimpleGrain")]
        public void SimpleGrain_AsyncMethods()
        {
            ISimpleGrainWithAsyncMethods grain = GrainClient.GrainFactory.GetGrain<ISimpleGrainWithAsyncMethods>(GetRandomGrainId());
            Task setPromise = grain.SetA_Async(10);
            setPromise.Wait();

            setPromise = grain.SetB_Async(30);
            setPromise.Wait();

            Task<int> intPromise = grain.GetAxB_Async();
            Assert.Equal(300, intPromise.Result);
        }

        [Fact, TestCategory("Functional"), TestCategory("SimpleGrain")]
        public void SimpleGrain_PromiseForward()
        {
            ISimpleGrain forwardGrain = GrainClient.GrainFactory.GetGrain<IPromiseForwardGrain>(GetRandomGrainId());
            Task<int> promise = forwardGrain.GetAxB(5, 6);
            int result = promise.Result;
            Assert.Equal(30, result);
        }

        [Fact, TestCategory("Functional"), TestCategory("SimpleGrain")]
        public void SimpleGrain_GuidDistribution()
        {
            int n = 0x1111;
            CreateGR(n, 1);
            CreateGR(n + 1, 1);
            CreateGR(n + 2, 1);
            CreateGR(n + 3, 1);
            CreateGR(n + 4, 1);

            Logger.Info("================");

            CreateGR(n, 2);
            CreateGR(n + 1, 2);
            CreateGR(n + 2, 2);
            CreateGR(n + 3, 2);
            CreateGR(n + 4, 2);

            Logger.Info("DONE.");
        }

        private void CreateGR(int n, int type)
        {
            Guid guid;
            if (type == 1)
            {
                guid = Guid.Parse(string.Format("00000000-0000-0000-0000-{0:X12}", n));
            }
            else
            {
                guid = Guid.Parse(string.Format("{0:X8}-0000-0000-0000-000000000000", n));
            }
            IEchoGrain grain = GrainClient.GrainFactory.GetGrain<IEchoGrain>(guid);
            GrainId grainId = ((GrainReference)grain.AsReference<IEchoGrain>()).GrainId;
            output.WriteLine("Guid = {0}, Guid.HashCode = x{1:X8}, GrainId.HashCode = x{2:X8}, GrainId.UniformHashCode = x{3:X8}", guid, guid.GetHashCode(), grainId.GetHashCode(), grainId.GetUniformHashCode());
        }

        [Fact, TestCategory("Revisit"), TestCategory("Observers")]
        public void ObserverTest_Disconnect()
        {
            ObserverTest_Disconnect(false);
        }

        [Fact, TestCategory("Revisit"), TestCategory("Observers")]
        public void ObserverTest_Disconnect2()
        {
            ObserverTest_Disconnect(true);
        }

        public void ObserverTest_Disconnect(bool observeTwice)
        {
            // this is for manual repro & validation in the debugger
            // wait to send event because it takes 60s to drop client grain
            //var simple1 = SimpleGrainTests.GetSimpleGrain();
            //var simple2 = SimpleGrainFactory.Cast(Domain.Current.Create(typeof(ISimpleGrain).FullName,
            //    new Dictionary<string, object> { { "EventDelay", 70000 } }));
            //var result = new ResultHandle();
            //var callback = new SimpleGrainObserver((a, b, r) =>
            //{
            //    r.Done = (a == 10);
            //    output.WriteLine("Received observer callback: A={0} B={1} Done={2}", a, b, r.Done);
            //}, result);
            //var observer = SimpleGrainObserverFactory.CreateObjectReference(callback);
            //if (observeTwice)
            //{
            //    simple1.Subscribe(observer).Wait();
            //    simple1.SetB(1).Wait(); // send a message to the observer to get it in the cache
            //}
            //simple2.Subscribe(observer).Wait();
            //simple2.SetA(10).Wait();
            //Thread.Sleep(2000);
            //Client.Uninitialize();
            //var timeout80sec = TimeSpan.FromSeconds(80);
            //Assert.False(result.WaitForFinished(timeout80sec), "WaitforFinished Timeout=" + timeout80sec);
            //// prevent silo from shutting down right away
            //Thread.Sleep(Debugger.IsAttached ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(5));
        }
    }
}
