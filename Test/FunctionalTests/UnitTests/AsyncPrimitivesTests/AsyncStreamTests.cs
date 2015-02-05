using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Scheduler;
using Orleans.RuntimeCore;

namespace UnitTests
{
    [TestClass]
    public class AsyncStreamTests
    {
        const int WAIT_TIMEOUT = 15 * 1000 *  1000;
        public AsyncStreamTests()
        {
        }

        [TestInitialize]
        [TestCleanup]
        public void MyTestCleanup()
        {
            OrleansTask.Reset();
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("AsynchronyPrimitives")]
        public void AsyncStream_PromptWhile_IEnumerable()
        {
            Console.WriteLine("\n AsyncStream_PromptWhile_IEnumerable()...");
            int[] array = {0, 1, 2, 3, 4, 5, 6, 7, 8, 9} ;
            AsyncStream_OLD<int> stream1 = array.AsAsyncStream();
            AsyncStream_While(stream1, true, false);

            List<int> input = new List<int>(array);
            AsyncStream_OLD<int> stream2 = input.AsAsyncStream();
            AsyncStream_While(stream2, true, false);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("AsynchronyPrimitives")]
        public void AsyncStream_PromptWhile_Producer()
        {
            Console.WriteLine("\n AsyncStream_PromptWhile_Producer()...");
            Producer producer = new Producer();
            AsyncStream_OLD<int> stream = producer.AsAsyncStream();
            producer.Produce().Ignore();
            AsyncStream_While(stream, true, true);
        }

        private void AsyncStream_While(AsyncStream_OLD<int> stream, bool prompt, bool asyncProduction)
        {
            List<int> list1 = new List<int>();
            List<int> list2 = new List<int>();
            int count = 0;
            int predicateCounter = 0;
            //int while2PredicateCounter = 0;

            Console.WriteLine("Sleeping...");
            Thread.Sleep(2000);
            Console.WriteLine("Calling While");
            Func<int, bool> predicate1 = ((int index) =>
                {
                    Console.WriteLine("Calling predicate1 index={0}, predicateCounter={1}", index, predicateCounter);
                    Assert.AreEqual<int>(index, predicateCounter);
                    bool shouldProceed = count < 5;
                    if (shouldProceed)
                        predicateCounter++;
                    return shouldProceed;
                });
            Action<int, int> body1Sync = ((int item, int index) =>
            {
                list1.Add(item);
                Console.WriteLine("Received1 item {0}", item);
                count++;
            });
            Func<int, int, AsyncCompletion> body1Async = ((int item, int index) =>
            {
                return AsyncCompletion.StartNew(() =>
                {
                    Console.WriteLine("Received item {0}, processing...", item);
                    Thread.Sleep(500);
                    //Console.WriteLine("Done processing item {0}.", item);
                    count++;
                    list1.Add(item);
                });
            });

            Func<int, bool> predicate2 = ((int index) =>
                {
                    Console.WriteLine("Calling predicate2 index={0}, predicateCounter={1}", index, predicateCounter);
                    Assert.AreEqual<int>(index, predicateCounter);
                    predicateCounter++; 
                    return true; 
                });
            Action<int, int> body2Sync = ((int item, int index) =>
            {
                list2.Add(item);
                Console.WriteLine("Received2 item {0}", item);
                count++;
            });
            Func<int, int, AsyncCompletion> body2Async = ((int item, int index) =>
            {
                return AsyncCompletion.Done.ContinueWith(()=>
                {
                    return AsyncCompletion.StartNew(() =>
                    {
                        Console.WriteLine("Received2 item {0}, processing...", item);
                        Thread.Sleep(500);
                        //Console.WriteLine("Done2 processing item {0}.", item);
                        count++;
                        list2.Add(item);
                    });
                });
            });

            AsyncCompletion done = prompt ? stream.While(predicate1, body1Sync) : stream.While(predicate1, body1Async);
            Console.WriteLine("While scheduled");
            AsyncCompletion done2 = prompt ? stream.While(predicate2, body2Sync) : stream.While(predicate2, body2Async);
            Console.WriteLine("While2 scheduled");
            try
            {
                done2.Wait(WAIT_TIMEOUT);
                if (asyncProduction)
                    Assert.Fail("Should have thrown.");
            }
            catch (Exception exc)
            {
                if (asyncProduction)
                {
                    Assert.AreEqual(typeof(System.InvalidOperationException), exc.GetBaseException().GetType());
                    Console.WriteLine("While2 has thrown correctly.");
                }
                else
                {
                    throw;
                }
            }
            AsyncCompletion done3 = done.ContinueWith(() => prompt ? stream.While(predicate2, body2Sync) : stream.While(predicate2, body2Async));
            done3.Wait(WAIT_TIMEOUT);
            Assert.AreEqual<int>(5, list1.Count);
            Assert.AreEqual<int>(5, list2.Count);
            for (int i = 0; i < 5; i++)
            {
                Assert.AreEqual<int>(i, list1[i]);
                Assert.AreEqual<int>(i+5, list2[i]);
            }
            Assert.AreEqual<int>(11, predicateCounter);
            //Assert.AreEqual<int>(6, while2PredicateCounter);
            Console.WriteLine("TestPromptWhile() done.");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("AsynchronyPrimitives")]
        public void AsyncStream_AsyncWhile()
        {
            Console.WriteLine("\n AsyncStream_AsyncWhile()...");
            Producer producer = new Producer();
            AsyncStream_OLD<int> stream = producer.AsAsyncStream();
            AsyncCompletion ac = producer.Produce();
            AsyncStream_While(stream, false, true);
            ac.Wait(WAIT_TIMEOUT);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("AsynchronyPrimitives")]
        public void AsyncStream_Select()
        {
            List<string> list = new List<string>();
            Console.WriteLine("\n\nTestSelect()...");

            Producer producer = new Producer();
            AsyncStream_OLD<int> stream = producer.AsAsyncStream<int>();
            AsyncCompletion producePromise = producer.Produce();

            Console.WriteLine("Sleeping...");
            Thread.Sleep(2000);
            Console.WriteLine("Calling Select");

            AsyncStream_OLD<string> strings = stream.Select<string>((int item) => (item * 10).ToString());

            AsyncCompletion done = strings.ForEach((string s) =>
            {
                return AsyncCompletion.StartNew(() =>
                {
                    Console.WriteLine(@"Received item ""{0}"", processing...", s);
                    Thread.Sleep(500);
                    Console.WriteLine(@"Done processing item ""{0}"".", s);
                    list.Add(s);
                });
            });
            Console.WriteLine("Select scheduled");

            done.Wait(WAIT_TIMEOUT);

            Assert.AreEqual<int>(10, list.Count);
            for (int i = 0; i < 10; i++)
            {
                Assert.AreEqual<string>((i*10).ToString(), list[i]);
            }
            producePromise.Wait(WAIT_TIMEOUT);
            Console.WriteLine("TestSelect() is done.");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("AsynchronyPrimitives")]
        public void AsyncStream_Take()
        {
            List<string> list = new List<string>();
            Console.WriteLine("\n\nTestTake()...");

            Producer producer = new Producer();
            AsyncStream_OLD<int> stream = producer.AsAsyncStream<int>();
            AsyncCompletion producePromise = producer.Produce();

            Console.WriteLine("Sleeping...");
            Thread.Sleep(2000);
            Console.WriteLine("Calling Select");

            List<int> output = stream.Take(20).GetValue();
            Assert.AreEqual<int>(10, output.Count);
            producePromise.Wait(WAIT_TIMEOUT);
            Console.WriteLine("TestSelect() is done.");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("AsynchronyPrimitives")]
        public void AsyncStream_SelectMany()
        {
            Console.WriteLine("\n\nAsyncStream_SelectMany()...");

            Producer producer = new Producer();
            AsyncStream_OLD<int> stream = producer.AsAsyncStream<int>();
            AsyncCompletion producePromise = producer.Produce();

            AsyncStream_OLD<string> strings = stream.SelectMany<string>((int i) => (new string[] { i.ToString(), (i*100).ToString(), (i*1000).ToString() }).AsAsyncStream());
            List<string> list = new List<string>();
            AsyncCompletion done = strings.ForEach((string s) => AsyncCompletion.StartNew(() => list.Add(s)));
            done.Wait(WAIT_TIMEOUT);
            list.ForEach(i => Console.Write(i + " "));

            Assert.AreEqual<int>(30, list.Count);
            for (int i = 0; i < 10; i+=3)
            {
                int item = i / 3;
                Assert.AreEqual<string>(item.ToString(), list[i]);
                Assert.AreEqual<string>((item * 100).ToString(), list[i + 1]);
                Assert.AreEqual<string>((item * 1000).ToString(), list[i + 2]);
            }
            producePromise.Wait(WAIT_TIMEOUT);
            //----------------------------------------------------------------
            producer = new Producer();
            stream = producer.AsAsyncStream<int>();
            producePromise = producer.Produce();

            strings = stream.SelectMany<double, string>(
                    (int i) => (new double[] { i * 10.5, i * 100.5, i * 1000.5 }).AsAsyncStream(),
                    (int i, double d) => d.ToString()
                );
            list.Clear();
            done = strings.ForEach((string s) => AsyncCompletion.StartNew(() => list.Add(s)));
            done.Wait(WAIT_TIMEOUT);
            list.ForEach(i => Console.Write(i + " "));

            Assert.AreEqual<int>(30, list.Count);
            for (int i = 0; i < 10; i += 3)
            {
                int item = i / 3;
                Assert.AreEqual<string>((item * 10.5).ToString(), list[i]);
                Assert.AreEqual<string>((item * 100.5).ToString(), list[i + 1]);
                Assert.AreEqual<string>((item * 1000.5).ToString(), list[i + 2]);
            }
            producePromise.Wait(WAIT_TIMEOUT);
            Console.WriteLine("AsyncStream_SelectMany() is done.");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("AsynchronyPrimitives")]
        public void AsyncStream_Where()
        {
            Console.WriteLine("\n\nTestWhere()...");
            List<int> list = new List<int>();
            Producer producer = new Producer();
            AsyncStream_OLD<int> stream = producer.AsAsyncStream<int>();
            AsyncCompletion producePromise = producer.Produce();

            Console.WriteLine("Sleeping...");
            Thread.Sleep(2000);
            Console.WriteLine("Calling Where");

            AsyncStream_OLD<int> strings = stream.Where((int item) => item % 2 == 0);

            AsyncCompletion done = strings.ForEach((int i) =>
            {
                return AsyncCompletion.StartNew(() =>
                {
                    Console.WriteLine(@"Received item {0}, processing...", i);
                    Thread.Sleep(500);
                    Console.WriteLine(@"Done processing item {0}.", i);
                    list.Add(i);
                });
            });
            Console.WriteLine("Where scheduled");
            done.Wait(WAIT_TIMEOUT);

            Assert.AreEqual<int>(5, list.Count);
            for (int i = 0; i < 5; i++)
            {
                Assert.AreEqual<int>(i*2, list[i]);
            }
            producePromise.Wait(WAIT_TIMEOUT);
            Console.WriteLine("TestWhere() is done.");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("AsynchronyPrimitives")]
        public void AsyncStream_Break()
        {
            Console.WriteLine("\n\nAsyncStream_Break()...");
            Producer producer = new Producer();
            AsyncStream_OLD<int> stream = producer.AsAsyncStream<int>();
            Console.WriteLine("Producing first stream");
            AsyncCompletion producePromise = producer.Produce();
            List<int> list = new List<int>();
            list = stream.ToList().GetValue();
            Assert.AreEqual<int>(10, list.Count);
            for (int i = 0; i < 10; i++)
            {
                Assert.AreEqual<int>(i, list[i]);
            }
            stream.Dispose();
            producePromise.Wait(WAIT_TIMEOUT);
            //----------------------------------------------------------------
            producer = new Producer();
            stream = producer.AsAsyncStream<int>();
            Console.WriteLine("Producing second stream");
            producePromise = producer.StartProducing();
            list = new List<int>();
            AsyncCompletion while1 = stream.While(() => list.Count < 5, (int i) => list.Add(i));
            while1.Wait(WAIT_TIMEOUT);
            Assert.AreEqual<int>(5, list.Count);

            producer.DoBreak().Wait(WAIT_TIMEOUT);
            AsyncCompletion while2 = stream.While(() => true, (int i) =>
                {
                    Console.WriteLine(@"While2 Received item {0}, processing...", i);
                    Assert.Fail("While2 Iteration should not happen. Stream is broken.");
                });
            try
            {
                while2.Wait(WAIT_TIMEOUT);
                Assert.Fail("Wait should have thrown. Stream is broken.");
            }
            catch (Exception exc)
            {
                Assert.AreEqual(typeof(System.ArithmeticException), exc.GetBaseException().GetType());
                Console.WriteLine("Thrown correctly 1.");
            }
            stream.Dispose();
            producePromise.Wait(WAIT_TIMEOUT);
            //----------------------------------------------------------------
            producer = new Producer();
            stream = producer.AsAsyncStream<int>();
            Console.WriteLine("Producing third stream");
            producePromise = producer.StartProducing();
            list = new List<int>();
            while1 = stream.While(() => list.Count < 5, (int i) => list.Add(i));
            while1.Wait(WAIT_TIMEOUT);
            Assert.AreEqual<int>(5, list.Count);
            
            while2 = stream.While(() => true, (int i) =>
            {
                Console.WriteLine(@"While3 Received item {0}, processing...", i);
            });

            producer.DoBreak().Wait(WAIT_TIMEOUT);

            try
            {
                while2.Wait(WAIT_TIMEOUT);
                Assert.Fail("Wait should have thrown. Stream is broken.");
            }
            catch (Exception exc)
            {
                Assert.AreEqual(typeof(System.ArithmeticException), exc.GetBaseException().GetType());
                Console.WriteLine("Thrown correctly 2.");
            }
            //----------------------------------------------------------------
            stream.Dispose();
            stream.Dispose();
            producePromise.Wait(WAIT_TIMEOUT);
            Console.WriteLine("AsyncStream_Break() is done.");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("AsynchronyPrimitives")]
        public void AsyncStream_Close()
        {
            Console.WriteLine("\n AsyncStream_Close()...");
            Producer producer = new Producer();
            AsyncStream_OLD<int> stream = producer.AsAsyncStream<int>();
            Console.WriteLine("Producing first stream");
            AsyncCompletion producePromise = producer.StartProducing();
            List<int> list = new List<int>();
            AsyncCompletion while1 = stream.While(() => list.Count < 5, (int i) => list.Add(i));
            while1.Wait(WAIT_TIMEOUT);
            Assert.AreEqual<int>(5, list.Count);
            stream.Dispose();

            AsyncCompletion while2 = stream.While(() => true, (int i) =>
            {
                Console.WriteLine(@"While2 Received item {0}, processing...", i);
                Assert.Fail("While2 Iteration should not happen. Stream is closed.");
            });
            try
            {
                while2.Wait(WAIT_TIMEOUT);
                Assert.Fail("Wait should have thrown. Stream is closed.");
            }
            catch (Exception exc)
            {
                Assert.AreEqual(typeof(System.ObjectDisposedException), exc.GetBaseException().GetType());
                Console.WriteLine("Thrown correctly 1.");
            }
            //----------------------------------------------------------------
            stream.Dispose();
            stream.Dispose();
            producer.DoBreak().Wait(WAIT_TIMEOUT);
            producePromise.Wait(WAIT_TIMEOUT);
            Console.WriteLine("AsyncStream_Close() is done.");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("AsynchronyPrimitives")]
        public void AsyncStream_BrokenProducer()
        {
            Console.WriteLine("\n AsyncStream_BrokenProducer()...");
            IAsyncObservable_OLD<int> producer = new BrokenProducer();
            AsyncStream_OLD<int> stream = producer.AsAsyncStream<int>();

            try
            {
                AsyncCompletion while1 = stream.ForEach((int i) => { });
                while1.Wait(WAIT_TIMEOUT);
                Assert.Fail("Subscribe should have thrown. Stream is broken.");
            }
            catch (Exception exc)
            {
                Assert.AreEqual(typeof(System.ArgumentException), exc.GetBaseException().GetType());
                Console.WriteLine("Thrown correctly 1.");
            }
            stream.Dispose();
            Console.WriteLine("AsyncStream_BrokenProducer() is done.");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("AsynchronyPrimitives")]
        public void AsyncStream_BreakingOperation()
        {
            Console.WriteLine("\n AsyncStream_BreakingOperation()...");
            Producer producer = new Producer();
            AsyncStream_OLD<int> stream = producer.AsAsyncStream<int>();
            AsyncCompletion producePromise = producer.StartProducing();

            try
            {
                AsyncCompletion while1 = stream.ForEach((int i) => 
                {
                    if (i > 2)
                        throw new ApplicationException();
                });
                while1.Wait(WAIT_TIMEOUT);
                Assert.Fail("ForEach1 should have thrown. Stream is broken.");
            }
            catch (Exception exc)
            {
                Assert.AreEqual(typeof(System.ApplicationException), exc.GetBaseException().GetType());
                Console.WriteLine("Thrown correctly 1.");
            }

            try
            {
                AsyncCompletion while1 = stream.ForEach((int i) =>
                {
                    Assert.Fail("ForEach2 should not have executed. Stream is broken.");
                });
                while1.Wait(WAIT_TIMEOUT);
                Assert.Fail("ForEach2 should have thrown. Stream is broken.");
            }
            catch (Exception exc)
            {
                Assert.AreEqual(typeof(System.ApplicationException), exc.GetBaseException().GetType());
                Console.WriteLine("Thrown correctly 2.");
            }
            stream.Dispose();
            producePromise.Wait(WAIT_TIMEOUT);
            Console.WriteLine("AsyncStream_BrokenProducer() is done.");
        }

        private class Producer : IAsyncObservable_OLD<int>
        {
            IAsyncObserver_OLD<int> consumer;

            public Producer()
            {
            }

            #region IAsyncObservable<int> Members

            public AsyncCompletion Subscribe(IAsyncObserver_OLD<int> consumer)
            {
                this.consumer = consumer;
                return AsyncCompletion.Done;
            }

            public AsyncCompletion UnSubscribe(IAsyncObserver_OLD<int> consumer)
            {
                Console.WriteLine("\tUnSubscribe called");
                return AsyncCompletion.Done;
            }
            #endregion
        
        
            internal AsyncCompletion Produce()
            {
                return StartProducing().ContinueWith(() => DoFinish());
            }

            internal AsyncCompletion StartProducing()
            {
                return AsyncCompletion.StartNew(() =>
                {
                    int counter = 0;
                    while (counter < 10)
                    {
                        Thread.Sleep(500);
                        Console.WriteLine("\tSending item {0}", counter);
                        consumer.OnNext(counter++).Ignore();
                        if (counter >= 5) // send two in a row
                        {
                            {
                                Console.WriteLine("\tSending item {0}", counter);
                                consumer.OnNext(counter).Ignore();
                            }
                            counter++;
                        }
                    }
                    //consumer.OnCompleted().Ignore();
                });
            }

            internal AsyncCompletion DoBreak()
            {
                Console.WriteLine("\tDoBreak called");
                return consumer.OnError(new System.ArithmeticException("InitiatedBreak()"));
            }

            internal AsyncCompletion DoFinish()
            {
                return consumer.OnCompleted();
            }
        }

        private class BrokenProducer : IAsyncObservable_OLD<int>
        {
            public BrokenProducer(){}

            public AsyncCompletion Subscribe(IAsyncObserver_OLD<int> consumer)
            {
                return AsyncCompletion.GenerateFromException(new ArgumentException("Trying to subscribe to a broken producer"));
            }

            public AsyncCompletion UnSubscribe(IAsyncObserver_OLD<int> consumer)
            {
                return AsyncCompletion.GenerateFromException(new NotImplementedException());
            }
        }
    }
}

