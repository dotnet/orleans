using System.Net;
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
            loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(output) });
            rootContext = new UnitTestSchedulingContext()
            {
                Scheduler = SchedulingHelper.CreateWorkItemGroupForTesting(rootContext, loggerFactory)
            };
            localGrainDirectory = new MockLocalGrainDirectory(
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(200));
            target = new DhtGrainLocator(localGrainDirectory, rootContext);
        }

        [Fact]
        public async Task SingleDeactivation()
        {
            foreach (var cause in (UnregistrationCause[]) Enum.GetValues(typeof(UnregistrationCause)))
             {
                var activationAddress = GenerateActivationAddress();

                await target.Unregister(activationAddress, cause);

                Assert.Single(localGrainDirectory.UnregistrationReceived);
                Assert.Equal(cause, localGrainDirectory.UnregistrationReceived[0].cause);
                Assert.Equal(activationAddress, localGrainDirectory.UnregistrationReceived[0].activationAddress);

                localGrainDirectory.Reset();
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
                    tasks.Add(target.Unregister(addr, cause));
                }

                await Task.WhenAll(tasks);

                Assert.True(batchn > localGrainDirectory.UnregistrationCounter);

                Assert.All(localGrainDirectory.UnregistrationReceived, item => Assert.Equal(cause, item.cause));
                Assert.Equal(addresses, localGrainDirectory.UnregistrationReceived.Select(item => item.activationAddress));

                localGrainDirectory.Reset();
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
                    tasks.Add(target.Unregister(addr, cause));
                }
            }

            await Task.WhenAll(tasks);

            Assert.All(localGrainDirectory.UnregistrationReceived, item => Assert.Equal(map[item.activationAddress], item.cause));
            Assert.Equal(addresses.ToHashSet(), localGrainDirectory.UnregistrationReceived.Select(item => item.activationAddress).ToHashSet());

            localGrainDirectory.Reset();
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
