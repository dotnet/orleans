using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests
{
    public class DropMessageTests : HostedTestClusterEnsureDefaultStarted
    {
        private TimeSpan responseTimeout;

        public DropMessageTests(DefaultClusterFixture fixture) : base(fixture)
        {
            responseTimeout = fixture.HostedCluster.ClusterConfiguration.Globals.ResponseTimeout;
        }

        [Fact, TestCategory("SlowBVT")]
        public async Task CallThatShouldHaveBeenDroppedNotExecutedTest()
        {
            var target = Client.GetGrain<ILongRunningTaskGrain<int>>(Guid.NewGuid());

            var tasks = new[]
            {
                // First call should be successful, but client will not receive the response
                target.LongRunningTask(1, responseTimeout + TimeSpan.FromSeconds(5)),
                // Second call should be dropped by the silo
                target.LongRunningTask(2, TimeSpan.Zero)
            };

            foreach (var task in tasks)
            {
                try
                {
                    await task;
                }
                catch (TimeoutException)
                {
                }
            }

            Assert.Equal(1, await target.GetLastValue());
        }
    }
}
