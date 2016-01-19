using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Streams;

namespace UnitTests.OrleansRuntime.Streams
{
    [TestClass]
    public class SubscriptionMarkerTests
    {
        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming")]
        public void MarkAsImplicitSubscriptionTest()
        {
            Guid guid = Guid.Empty;

            Assert.IsFalse(SubscriptionMarker.IsImplicitSubscription(guid));

            Guid markedGuid = SubscriptionMarker.MarkAsImplictSubscriptionId(guid);

            Assert.IsTrue(SubscriptionMarker.IsImplicitSubscription(markedGuid));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming")]
        public void MarkAsExplicitSubscriptionTest()
        {
            byte[] guidBytes = Enumerable.Range(0, 16).Select(i => (byte)0xff).ToArray();
            var guid = new Guid(guidBytes);

            Assert.IsTrue(SubscriptionMarker.IsImplicitSubscription(guid));

            Guid markedGuid = SubscriptionMarker.MarkAsExplicitSubscriptionId(guid);

            Assert.IsFalse(SubscriptionMarker.IsImplicitSubscription(markedGuid));
        }
    }
}
