﻿using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace UnitTests.General
{
    //using ValueUpdateEventArgs = MultifacetGrainClient.ValueUpdateEventArgs;

    [TestClass]
    public class MultifacetGrainTest : UnitTestSiloHost
    {
        IMultifacetWriter writer;
        IMultifacetReader reader;
        //int eventCounter;
        const int EXPECTED_NUMBER_OF_EVENTS = 4;
        private TimeSpan timeout = TimeSpan.FromSeconds(5);

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        public void RWReferences()
        {
            writer = GrainClient.GrainFactory.GetGrain<IMultifacetWriter>(4);
            reader = writer.AsReference<IMultifacetReader>();
            
            int x = 1234;
            bool ok = writer.SetValue(x).Wait(timeout);
            if (!ok) throw new TimeoutException();
            int y = reader.GetValue().Result;
            Assert.AreEqual(x, y);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        [ExpectedException(typeof(InvalidCastException))]
        public void RWReferencesInvalidCastException()
        {
            reader = GrainClient.GrainFactory.GetGrain<IMultifacetReader>(5);
            writer = (IMultifacetWriter)reader;
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        public async Task MultifacetFactory()
        {
            IMultifacetFactoryTestGrain factory = GrainClient.GrainFactory.GetGrain<IMultifacetFactoryTestGrain>(0);
            IMultifacetTestGrain grain = GrainClient.GrainFactory.GetGrain<IMultifacetTestGrain>(6);
            IMultifacetWriter writer = await factory.GetWriter(grain /*"MultifacetFactory"*/);
            IMultifacetReader reader = await factory.GetReader(grain /*"MultifacetFactory"*/);
            writer.SetValue(5).Wait();
            int v = reader.GetValue().Result;
            Assert.AreEqual(5, v);
            
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        public async Task Multifacet_InterfacesAsArguments()
        {
            IMultifacetFactoryTestGrain factory = GrainClient.GrainFactory.GetGrain<IMultifacetFactoryTestGrain>(1);
            IMultifacetTestGrain grain = GrainClient.GrainFactory.GetGrain<IMultifacetTestGrain>(7);
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
