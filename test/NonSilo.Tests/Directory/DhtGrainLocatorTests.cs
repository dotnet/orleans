using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using TestExtensions;
using UnitTests.SchedulerTests;
using UnitTests.TesterInternal;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.Directory
{
    [TestCategory("BVT"), TestCategory("Directory")]
    public class DhtGrainLocatorTests
    {
        private readonly DhtGrainLocator target;
        private readonly MockLocalGrainDirectory localGrainDirectory;
        private readonly ITestOutputHelper output;
        private readonly LoggerFactory loggerFactory;
        private readonly UnitTestSchedulingContext rootContext;

        public DhtGrainLocatorTests(ITestOutputHelper output)
        {
            this.output = output;
            this.loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(output) });
            this.rootContext = new UnitTestSchedulingContext()
            {
                Scheduler = SchedulingHelper.CreateWorkItemGroupForTesting(this.rootContext, this.loggerFactory)
            };
            this.localGrainDirectory = new MockLocalGrainDirectory(
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(200));
            this.target = new DhtGrainLocator(this.localGrainDirectory, this.rootContext);
        }

        [Fact]
        public async Task SingleDeactivation()
        {
            foreach (var cause in (UnregistrationCause[]) Enum.GetValues(typeof(UnregistrationCause)))
             {
                var activationAddress = GenerateActivationAddress();

                await this.target.Unregister(activationAddress, cause);

                Assert.Single(this.localGrainDirectory.UnregistrationReceived);
                Assert.Equal(cause, this.localGrainDirectory.UnregistrationReceived[0].cause);
                Assert.Equal(activationAddress, this.localGrainDirectory.UnregistrationReceived[0].activationAddress);

                this.localGrainDirectory.Reset();
            }
        }

        [Fact]
        public async Task MultipleDeactivations()
        {
            foreach (var cause in (UnregistrationCause[])Enum.GetValues(typeof(UnregistrationCause)))
            {
                var batchn = 100;
                var addresses = new List<GrainAddress>();
                var tasks = new List<Task>();

                for (var i = 0; i < batchn; i++)
                {
                    addresses.Add(GenerateActivationAddress());
                }

                foreach (var addr in addresses)
                {
                    tasks.Add(this.target.Unregister(addr, cause));
                }

                await Task.WhenAll(tasks);

                Assert.True(batchn > this.localGrainDirectory.UnregistrationCounter);

                Assert.All(this.localGrainDirectory.UnregistrationReceived, item => Assert.Equal(cause, item.cause));
                Assert.Equal(addresses, this.localGrainDirectory.UnregistrationReceived.Select(item => item.activationAddress));

                this.localGrainDirectory.Reset();
            }
        }

        [Fact]
        public async Task MultipleMixedDeactivations()
        {
            var batchn = 12;
            var addresses = new List<GrainAddress>();
            var tasks = new List<Task>();

            var map = new Dictionary<GrainAddress, UnregistrationCause>();

            foreach (var cause in (UnregistrationCause[])Enum.GetValues(typeof(UnregistrationCause)))
            {
                for (var i = 0; i < batchn; i++)
                {
                    var addr = GenerateActivationAddress();
                    addresses.Add(addr);
                    map.Add(addr, cause);
                    tasks.Add(this.target.Unregister(addr, cause));
                }
            }

            await Task.WhenAll(tasks);

            Assert.All(this.localGrainDirectory.UnregistrationReceived, item => Assert.Equal(map[item.activationAddress], item.cause));
            Assert.Equal(addresses.ToHashSet(), this.localGrainDirectory.UnregistrationReceived.Select(item => item.activationAddress).ToHashSet());

            this.localGrainDirectory.Reset();
        }

        private int generation = 0;
        private GrainAddress GenerateActivationAddress()
        {
            var grainId = LegacyGrainId.GetGrainIdForTesting(Guid.NewGuid());
            var siloAddr = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), ++generation);

            return GrainAddress.NewActivationAddress(siloAddr, grainId);
        }
    }
}
