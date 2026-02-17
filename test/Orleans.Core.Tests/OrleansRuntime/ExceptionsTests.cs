using Orleans.Runtime;
using TestExtensions;
using UnitTests.Serialization;
using Xunit;

namespace UnitTests.OrleansRuntime
{
    /// <summary>
    /// Tests for Orleans exception serialization and round-trip testing.
    /// </summary>
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class ExceptionsTests
    {
        private readonly TestEnvironmentFixture fixture;

        public ExceptionsTests(TestEnvironmentFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_Exception_Orleans()
        {
            var original = new SiloUnavailableException("Some message");
            var output = this.fixture.Serializer.RoundTripSerializationForTesting(original);

            Assert.Equal(original.Message, output.Message);
        }
    }
}
