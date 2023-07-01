using Orleans.Streams;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams
{
    public class SubscriptionMarkerTests
    {
        [Fact, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming")]
        public void MarkAsImplicitSubscriptionTest()
        {
            Guid guid = Guid.Empty;

            Assert.False(SubscriptionMarker.IsImplicitSubscription(guid));

            Guid markedGuid = SubscriptionMarker.MarkAsImplictSubscriptionId(guid);

            Assert.True(SubscriptionMarker.IsImplicitSubscription(markedGuid));
        }

        [Fact, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming")]
        public void MarkAsExplicitSubscriptionTest()
        {
            byte[] guidBytes = Enumerable.Range(0, 16).Select(i => (byte)0xff).ToArray();
            var guid = new Guid(guidBytes);

            Assert.True(SubscriptionMarker.IsImplicitSubscription(guid));

            Guid markedGuid = SubscriptionMarker.MarkAsExplicitSubscriptionId(guid);

            Assert.False(SubscriptionMarker.IsImplicitSubscription(markedGuid));
        }
    }
}
