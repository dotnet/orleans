using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MultifacetGrain;

namespace UnitTests.General
{
    //using ValueUpdateEventArgs = MultifacetGrainClient.ValueUpdateEventArgs;

    [TestClass]
    public class MultifacetGrainTest : UnitTestBase
    {
        IMultifacetWriter writer;
        IMultifacetReader reader;
        //int eventCounter;
        const int EXPECTED_NUMBER_OF_EVENTS = 4;
        private TimeSpan timeout = TimeSpan.FromSeconds(5);

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void RWReferences()
        {
            writer = MultifacetWriterFactory.GetGrain(4);
            reader = MultifacetReaderFactory.Cast(writer);
            
            int x = 1234;
            bool ok = writer.SetValue(x).Wait(timeout);
            if (!ok) throw new TimeoutException();
            int y = reader.GetValue().Result;
            Assert.AreEqual(x, y);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        [ExpectedException(typeof(InvalidCastException))]
        public void RWReferencesInvalidCastException()
        {
            reader = MultifacetReaderFactory.GetGrain(5);
            writer = (IMultifacetWriter)reader;
        }

        //void writer_CommonEvent(object sender, ValueUpdateEventArgs e)
        //{
        //    IncrementEventCounter("writer_CommonEvent");
        //}

        //void writer_CommonEvent2(object sender, ValueUpdateEventArgs e)
        //{
        //    IncrementEventCounter("writer_CommonEvent2");
        //}

        //void reader_ValueUpdateEvent(object sender, ValueUpdateEventArgs e)
        //{
        //    Assert.AreEqual(3, e.Value);
        //    //int val = reader.Result.GetValue(5000);
        //    //Assert.AreEqual(3, val);
        //    IncrementEventCounter("reader_ValueUpdateEvent");
        //}

        //void reader_ValueUpdateEvent2(object sender, ValueUpdateEventArgs e)
        //{
        //    Assert.AreEqual(3, e.Value);
        //    //int val = reader.Result.GetValue(5000);
        //    //Assert.AreEqual(3, val);
        //    IncrementEventCounter("reader_ValueUpdateEvent2");
        //}

        //void IncrementEventCounter(string eventHandlerName)
        //{
        //    int counter = Interlocked.Increment(ref eventCounter);

        //    Console.WriteLine(String.Format("Event counter: {0}, event: {1}", counter, eventHandlerName));
            
        //    Assert.IsTrue(counter <= EXPECTED_NUMBER_OF_EVENTS);
                        
        //    if (counter == EXPECTED_NUMBER_OF_EVENTS)
        //        result.Continue = true;
        //}

        //[TestMethod]
        //public void DuplicateEvents()
        //{
        //    eventCounter = 0;
        //    result = new ResultHandle();
        //    writer = MultifacetWriterFactory.GetGrain(GetRandomGrainId());
        //    writer.CommonEvent += writer_CommonEvent;
        //    writer.ValueReadEvent += writer_ValueReadEvent;
        //    writer.SetValue(5).Wait();
        //    Thread.Sleep(2000); // wait for events to propagate

        //    Assert.AreEqual(1, eventCounter);
        //}

        //void writer_ValueReadEvent(object sender, ValueUpdateEventArgs e)
        //{
        //    IncrementEventCounter("writer_ValueReadEvent");
        //}

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public async Task MultifacetFactory()
        {
            IMultifacetFactoryTestGrain factory = MultifacetFactoryTestGrainFactory.GetGrain(0);
            IMultifacetTestGrain grain = MultifacetTestGrainFactory.GetGrain(6);
            IMultifacetWriter writer = await factory.GetWriter(grain /*"MultifacetFactory"*/);
            IMultifacetReader reader = await factory.GetReader(grain /*"MultifacetFactory"*/);
            writer.SetValue(5).Wait();
            int v = reader.GetValue().Result;
            Assert.AreEqual(5, v);
            
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public async Task Multifacet_InterfacesAsArguments()
        {
            IMultifacetFactoryTestGrain factory = MultifacetFactoryTestGrainFactory.GetGrain(1);
            IMultifacetTestGrain grain = MultifacetTestGrainFactory.GetGrain(7);
            factory.SetReader(grain).Wait();
            factory.SetWriter(grain).Wait();
            IMultifacetWriter writer = await factory.GetWriter();
            IMultifacetReader reader = await factory.GetReader();
            writer.SetValue(10).Wait();
            int v = reader.GetValue().Result;
            Assert.AreEqual(10, v);
        }
    }
}
