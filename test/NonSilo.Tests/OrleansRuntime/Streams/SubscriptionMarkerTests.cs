using Orleans.Streams;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams
{
    public class SubscriptionMarkerTests
    {
        [Fact, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming")]
        public void MarkAsImplicitSubscriptionTest()
        {
            var guid = Guid.Empty;

            Assert.False(SubscriptionMarker.IsImplicitSubscription(guid));

            var markedGuid = SubscriptionMarker.MarkAsImplictSubscriptionId(guid);

            Assert.True(SubscriptionMarker.IsImplicitSubscription(markedGuid));
        }

        [Fact, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming")]
        public void MarkAsExplicitSubscriptionTest()
        {
            var guidBytes = Enumerable.Range(0, 16).Select(i => (byte)0xff).ToArray();
            var guid = new Guid(guidBytes);

            Assert.True(SubscriptionMarker.IsImplicitSubscription(guid));

            var markedGuid = SubscriptionMarker.MarkAsExplicitSubscriptionId(guid);

            Assert.False(SubscriptionMarker.IsImplicitSubscription(markedGuid));
        }
    }
}
