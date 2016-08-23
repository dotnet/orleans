using System;
using System.Linq;
using System.Threading.Tasks;
using HelloWorld.Interfaces;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Xunit;

namespace SiloTests
{
    /// <summary>
    /// How this works:
    /// 
    /// 1. The class fixture will create an in-process test silo and client
    ///    environment that will be shared by all tests within this class.
    /// 2. The default test environment will contain a mini cluster of 2 silos, 
    ///    running in separate AppDomains from the test process. The silo
    ///    will be named Primary and Secondary_1.
    /// 3. The Orleans client environment will be initialized in the main AppDomain
    ///    where this test class is running, so each of the test cases can assume 
    ///    that everything is initialized at the start of their execution.
    /// 4. The configuration used for the test silos and test client are based on 
    ///    the <c>TestClusterOptions</c> config object which can be configured 
    ///    in the <c>OrleansSiloFixture</c>
    /// 5. There are also various utility methods in the <c>TestCluster</c> class
    ///    that allow silos to be stopped or restarted to allow tests to programmatically 
    ///    simulate some simple failure mode conditions.
    /// 6. Be aware that if you want to have multiple fixtures that create and tear down clusters,
    ///    you should disable parallelization if within the same test project, as it is not possible
    ///    to have 2 different grain clients talking to different silos.
    /// Note: These tests are an example of using xUnit to write unit tests, although
    ///       similar testing frameworks such as NUnit or MsTest could have been used.
    ///       Consider using a collection fixture (using <see cref="ICollectionFixture{TFixture}"/> as 
    ///       opposed to implementing <see cref="IClassFixture{TFixture}"/> in the test class) if you want
    ///       to reuse the same cluster accross many test classes.
    /// </summary>
    public class HelloWorldSiloTests : IClassFixture<OrleansSiloFixture>
    {
        private readonly OrleansSiloFixture _fixture;

        public HelloWorldSiloTests(OrleansSiloFixture fixture)
        {
            _fixture = fixture;
        }

        private IGrainFactory GrainFactory => _fixture.Cluster.GrainFactory;

        [Fact]
        public async Task SiloSayHelloTest()
        {
            // The Orleans silo / client test environment is already set up at this point.

            long id = new Random().Next();
            const string greeting = "Bonjour";

            IHello grain = GrainFactory.GetGrain<IHello>(id);
            
            // This will create and call a Hello grain with specified 'id' in one of the test silos.
            string reply = await grain.SayHello(greeting);
            
            Assert.NotNull(reply);
            Assert.Equal($"You said: '{greeting}', I say: Hello!", reply);
        }

        [Fact]
        public async Task SiloSayHelloArchiveTest()
        {
            long id = new Random().Next();
            const string greeting1 = "Bonjour";
            const string greeting2 = "Hei";

            IHelloArchive grain = GrainFactory.GetGrain<IHelloArchive>(id);

            // This will directly call the grain under test.
            await grain.SayHello(greeting1);
            await grain.SayHello(greeting2);

            var greetings = (await grain.GetGreetings()).ToList();

            Assert.Contains(greeting1, greetings);
            Assert.Contains(greeting2, greetings);
        }
    }

    /// <summary>
    /// Class fixture used to share the silos between multiple tests within a specific test class.
    /// </summary>
    public class OrleansSiloFixture : IDisposable
    {
        public TestCluster Cluster { get; }

        public OrleansSiloFixture()
        {
            GrainClient.Uninitialize();

            var options = new TestClusterOptions(initialSilosCount: 2);
            options.ClusterConfiguration.AddMemoryStorageProvider("Default");
            options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
            Cluster = new TestCluster(options);

            if (Cluster.Primary == null)
            {
                Cluster.Deploy();
            }
        }

        /// <summary>
        /// Clean up the test fixture once all the tests have been run
        /// </summary>
        public void Dispose()
        {
            Cluster.StopAllSilos();
        }
    }
}
