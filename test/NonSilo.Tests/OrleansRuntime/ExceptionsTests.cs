using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
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
            BufferPool.InitGlobalBufferPool(new ClientMessagingOptions());
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_Exception_Orleans()
        {
            var original = new SiloUnavailableException("Some message");
            var output = this.fixture.SerializationManager.RoundTripSerializationForTesting(original);

            Assert.Equal(original.Message, output.Message);
        }
    }
}
