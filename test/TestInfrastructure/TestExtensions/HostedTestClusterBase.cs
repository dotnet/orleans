using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using Orleans.TestingHost;
using Xunit;

namespace TestExtensions
{
    /// <summary>
    /// Base class that ensures a silo cluster is started with the default configuration, and avoids restarting it if the previous test used the same default base.
    /// </summary>
    [Collection("DefaultCluster")]
    public abstract class HostedTestClusterEnsureDefaultStarted : OrleansTestingBase
    {
        protected DefaultClusterFixture Fixture { get; private set; }
        protected TestCluster HostedCluster => Fixture.HostedCluster;

        protected IGrainFactory GrainFactory => HostedCluster.GrainFactory;

        protected IClusterClient Client => HostedCluster.Client;
        protected ILogger Logger => Client.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Application");

        protected HostedTestClusterEnsureDefaultStarted(DefaultClusterFixture fixture)
        {
            Fixture = fixture;
        }
    }

    public static class TestClusterExtensions
    {
        public static T RoundTripSerializationForTesting<T>(this TestCluster cluster, T value)
        {
            var serializer = cluster.ServiceProvider.GetRequiredService<Serializer>();
            return serializer.Deserialize<T>(serializer.SerializeToArray(value));
        }

        public static T DeepCopy<T>(this TestCluster cluster, T value)
        {
            var copier = cluster.ServiceProvider.GetRequiredService<DeepCopier>();
            return copier.Copy(value);
        }

        public static Serializer GetSerializer(this TestCluster cluster) => cluster.ServiceProvider.GetRequiredService<Serializer>();
    }
}
