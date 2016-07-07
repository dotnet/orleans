using System;
using HelloWorldGrains;
using HelloWorldInterfaces;
using Moq;
using Orleans;
using Orleans.Core;
using Orleans.Runtime;
using Xunit;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NonSiloTests
{
    public class HelloWorldNonSiloTests
    {
        private GrainCreator _grainCreator;

        public HelloWorldNonSiloTests()
        {
            var grainRuntime = Mock.Of<IGrainRuntime>();

            _grainCreator = new GrainCreator(grainRuntime);
        }

        private T CreateGrain<T>(long id) where T : Grain, IGrainWithIntegerKey
        {
            var identity = new Mock<IGrainIdentity>();
            identity.Setup(i => i.PrimaryKeyLong).Returns(id);

            var grain = _grainCreator.CreateGrainInstance(typeof(T), identity.Object);

            return grain as T;
        }

        [Fact]
        public async void NonSiloSayHelloTest()
        {
            // The mocked Orleans runtime is already set up at this point

            const long id = 0;
            const string greeting = "Bonjour";

            //Create a new instance of the grain. Notice this is the concrete grain type
            //that is being tested, not just the grain interface as with the silo test
            IHello grain = CreateGrain<HelloGrain>(id);

            // This will create and call a Hello grain with specified 'id' in one of the test silos.
            string reply = await grain.SayHello(greeting);

            Assert.IsNotNull(reply, "Grain replied with some message");
            string expected = string.Format("You said: '{0}', I say: Hello!", greeting);
            Assert.AreEqual(expected, reply, "Grain replied with expected message");
        }
    }
}
