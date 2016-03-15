using System;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using UnitTests.GrainInterfaces;
using Xunit;
using UnitTests.Tester;

namespace UnitTests.General
{
    //using ValueUpdateEventArgs = MultifacetGrainClient.ValueUpdateEventArgs;
    
    public class MultifacetGrainTest : HostedTestClusterEnsureDefaultStarted
    {
        IMultifacetWriter writer;
        IMultifacetReader reader;
        //int eventCounter;
        const int EXPECTED_NUMBER_OF_EVENTS = 4;
        private TimeSpan timeout = TimeSpan.FromSeconds(5);

        [Fact, TestCategory("Functional"), TestCategory("Cast")]
        public void RWReferences()
        {
            writer = GrainClient.GrainFactory.GetGrain<IMultifacetWriter>(GetRandomGrainId());
            reader = writer.AsReference<IMultifacetReader>();
            
            int x = 1234;
            bool ok = writer.SetValue(x).Wait(timeout);
            if (!ok) throw new TimeoutException();
            int y = reader.GetValue().Result;
            Assert.AreEqual(x, y);
        }

        [Fact, TestCategory("Functional"), TestCategory("Cast")]
        public void RWReferencesInvalidCastException()
        {
            Xunit.Assert.Throws<InvalidCastException>(() =>
            {
                reader = GrainClient.GrainFactory.GetGrain<IMultifacetReader>(GetRandomGrainId());
                writer = (IMultifacetWriter)reader;
            });
        }

        [Fact, TestCategory("Functional"), TestCategory("Cast")]
        public async Task MultifacetFactory()
        {
            IMultifacetFactoryTestGrain factory = GrainClient.GrainFactory.GetGrain<IMultifacetFactoryTestGrain>(GetRandomGrainId());
            IMultifacetTestGrain grain = GrainClient.GrainFactory.GetGrain<IMultifacetTestGrain>(GetRandomGrainId());
            IMultifacetWriter writer = await factory.GetWriter(grain /*"MultifacetFactory"*/);
            IMultifacetReader reader = await factory.GetReader(grain /*"MultifacetFactory"*/);
            writer.SetValue(5).Wait();
            int v = reader.GetValue().Result;
            Assert.AreEqual(5, v);
            
        }

        [Fact, TestCategory("Functional"), TestCategory("Cast")]
        public async Task Multifacet_InterfacesAsArguments()
        {
            IMultifacetFactoryTestGrain factory = GrainClient.GrainFactory.GetGrain<IMultifacetFactoryTestGrain>(GetRandomGrainId());
            IMultifacetTestGrain grain = GrainClient.GrainFactory.GetGrain<IMultifacetTestGrain>(GetRandomGrainId());
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
