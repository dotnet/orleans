using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime.Messaging;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Membership
{
    [TestCategory("BVT"), TestCategory("Membership")]
    public class ProbeRequestMonitorTests
    {
        [Fact]
        public void ElapsedSinceLastProbeRequest_UsesInjectedTimeAndResetsOnReceipt()
        {
            var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var timeProvider = new FakeTimeProvider(start);
            var monitor = new ProbeRequestMonitor(timeProvider);

            Assert.Null(monitor.ElapsedSinceLastProbeRequest);

            monitor.OnReceivedProbeRequest();
            Assert.Equal(TimeSpan.Zero, monitor.ElapsedSinceLastProbeRequest);

            timeProvider.Advance(TimeSpan.FromSeconds(17));
            Assert.Equal(TimeSpan.FromSeconds(17), monitor.ElapsedSinceLastProbeRequest);

            monitor.OnReceivedProbeRequest();
            Assert.Equal(TimeSpan.Zero, monitor.ElapsedSinceLastProbeRequest);

            timeProvider.Advance(TimeSpan.FromMilliseconds(250));
            Assert.Equal(TimeSpan.FromMilliseconds(250), monitor.ElapsedSinceLastProbeRequest);
            Assert.Equal(start.AddSeconds(17).AddMilliseconds(250), timeProvider.GetUtcNow());
        }
    }
}
