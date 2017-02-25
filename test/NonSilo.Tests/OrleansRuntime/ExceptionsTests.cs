
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
            ActivationAddress activationAddress = ActivationAddress.NewActivationAddress(SiloAddress.NewLocalAddress(12345), GrainId.NewId());
            SiloAddress primaryDirectoryForGrain = SiloAddress.NewLocalAddress(6789);
           
            Catalog.DuplicateActivationException original = new Catalog.DuplicateActivationException(activationAddress, primaryDirectoryForGrain);
            Catalog.DuplicateActivationException output = TestingUtils.RoundTripDotNetSerializer(original, this.fixture.GrainFactory, this.fixture.SerializationManager);

            Assert.Equal(original.Message, output.Message);
            Assert.Equal(original.ActivationToUse, output.ActivationToUse);
            Assert.Equal(original.PrimaryDirectoryForGrain, output.PrimaryDirectoryForGrain);
        }
#endif

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_Exception_Orleans()
        {
            ActivationAddress activationAddress = ActivationAddress.NewActivationAddress(SiloAddress.NewLocalAddress(12345), GrainId.NewId());
            SiloAddress primaryDirectoryForGrain = SiloAddress.NewLocalAddress(6789);

            Catalog.DuplicateActivationException original = new Catalog.DuplicateActivationException(activationAddress, primaryDirectoryForGrain);
            Catalog.DuplicateActivationException output = this.fixture.SerializationManager.RoundTripSerializationForTesting(original);

            Assert.Equal(original.Message, output.Message);
            Assert.Equal(original.ActivationToUse, output.ActivationToUse);
            Assert.Equal(original.PrimaryDirectoryForGrain, output.PrimaryDirectoryForGrain);
        }
    }
}
