using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Xunit;

namespace UnitTests.UtilsTests
{
    /// <summary>
    /// Tests for utility functions including gateway URI conversion for IPv4 and IPv6 addresses.
    /// </summary>
    [TestCategory("Utils")]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    public class UtilsTests
    {
        private readonly ITestOutputHelper output;


        public UtilsTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact, TestCategory("BVT")]
        public void ToGatewayUriTest()
        {
            var ipv4 = new IPEndPoint(IPAddress.Any, 11111);
            var uri = ipv4.ToGatewayUri();
            Assert.Equal("gwy.tcp://0.0.0.0:11111/0", uri.ToString());

            var ipv4silo = SiloAddress.New(ipv4, 100);
            uri = ipv4silo.ToGatewayUri();
            Assert.Equal("gwy.tcp://0.0.0.0:11111/100", uri.ToString());

            var ipv6 = new IPEndPoint(IPAddress.IPv6Any, 11111);
            uri = ipv6.ToGatewayUri();
            Assert.Equal("gwy.tcp://[::]:11111/0", uri.ToString());

            var ipv6silo = SiloAddress.New(ipv6, 100);
            uri = ipv6silo.ToGatewayUri();
            Assert.Equal("gwy.tcp://[::]:11111/100", uri.ToString());
        }

        [Fact]
        public void GatewayConversions_NullReceiver_ThrowWithExactParameterName()
        {
            var endpointException = Assert.Throws<ArgumentNullException>(() => ((Uri)null!).ToIPEndPoint());
            Assert.Equal("uri", endpointException.ParamName);

            var addressException = Assert.Throws<ArgumentNullException>(() => ((Uri)null!).ToGatewayAddress());
            Assert.Equal("uri", addressException.ParamName);

            var uriException = Assert.Throws<ArgumentNullException>(() => ((SiloAddress)null!).ToGatewayUri());
            Assert.Equal("address", uriException.ParamName);

            var endpointUriException = Assert.Throws<ArgumentNullException>(() => ((IPEndPoint)null!).ToGatewayUri());
            Assert.Equal("ep", endpointUriException.ParamName);
        }

        [Fact]
        public void SafeExecute_NullAction_ThrowsWithExactParameterName()
        {
            var exception = Assert.Throws<ArgumentNullException>(() => Utils.SafeExecute((Action)null!));
            Assert.Equal("action", exception.ParamName);

            exception = Assert.Throws<ArgumentNullException>(() => Utils.SafeExecute((Action)null!, NullLogger.Instance));
            Assert.Equal("action", exception.ParamName);
        }

        [Fact]
        public void SafeExecute_ThrowingActions_SuppressExceptions()
        {
            var invocationCount = 0;

            Utils.SafeExecute(() =>
            {
                invocationCount++;
                throw new InvalidOperationException("expected");
            });
            Utils.SafeExecute(() =>
            {
                invocationCount++;
                throw new InvalidOperationException("expected");
            }, NullLogger.Instance);

            Assert.Equal(2, invocationCount);
        }

        [Fact]
        public void BatchIEnumerable_NullSequence_ThrowsAtCallTime()
        {
            IEnumerable<int> sequence = null!;

            var exception = Assert.Throws<ArgumentNullException>(() => sequence.BatchIEnumerable(2));

            Assert.Equal("sequence", exception.ParamName);
        }

        [Fact]
        public void BatchIEnumerable_ValidSequence_PreservesBatching()
        {
            var batches = Enumerable.Range(1, 5).BatchIEnumerable(2).Select(batch => batch.ToArray()).ToArray();

            Assert.Equal(3, batches.Length);
            Assert.Equal([1, 2], batches[0]);
            Assert.Equal([3, 4], batches[1]);
            Assert.Equal([5], batches[2]);
        }

        [Fact]
        public void Ignore_NullTask_ThrowsWithExactParameterName()
        {
            var exception = Assert.Throws<ArgumentNullException>(() => ((Task)null!).Ignore());

            Assert.Equal("task", exception.ParamName);
        }

        [Fact]
        public async Task SafeExecuteAsync_NullTask_ThrowsWithExactParameterName()
        {
            var exception = await Assert.ThrowsAsync<ArgumentNullException>(
                async () => await Utils.SafeExecuteAsync(null!));

            Assert.Equal("task", exception.ParamName);
        }
    }
}
