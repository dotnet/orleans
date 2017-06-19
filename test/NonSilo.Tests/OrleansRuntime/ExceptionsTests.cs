
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost.Utils;
using TestExtensions;
using Xunit;

namespace UnitTests.OrleansRuntime
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class ExceptionsTests
    {
        private readonly TestEnvironmentFixture fixture;

        public ExceptionsTests(TestEnvironmentFixture fixture)
        {
            this.fixture = fixture;
            BufferPool.InitGlobalBufferPool(new MessagingConfiguration(false));
        }

#if !NETSTANDARD
        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_Exception_DotNet()
        {
            var activationAddress = ActivationAddress.NewActivationAddress(SiloAddress.NewLocalAddress(12345), GrainId.NewId());
           
            var original = new Catalog.NonExistentActivationException("Some message", activationAddress, false);
            var output = TestingUtils.RoundTripDotNetSerializer(original, this.fixture.GrainFactory, this.fixture.SerializationManager);

            Assert.Equal(original.Message, output.Message);
            Assert.Equal(original.NonExistentActivation, output.NonExistentActivation);
            Assert.Equal(original.IsStatelessWorker, output.IsStatelessWorker);
        }
#endif

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_Exception_Orleans()
        {
            var activationAddress = ActivationAddress.NewActivationAddress(SiloAddress.NewLocalAddress(12345), GrainId.NewId());

            var original = new Catalog.NonExistentActivationException("Some message", activationAddress, false);
            var output = this.fixture.SerializationManager.RoundTripSerializationForTesting(original);

            Assert.Equal(original.Message, output.Message);
            Assert.Equal(original.NonExistentActivation, output.NonExistentActivation);
            Assert.Equal(original.IsStatelessWorker, output.IsStatelessWorker);
        }
    }
}
