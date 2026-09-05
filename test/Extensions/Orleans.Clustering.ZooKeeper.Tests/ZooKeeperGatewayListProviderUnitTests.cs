using System;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Membership;
using TestExtensions;
using Xunit;

namespace UnitTests.MembershipTests
{
    [TestCategory("Membership"), TestCategory("ZooKeeper")]
    [TestSuite("BVT")]
    [TestProvider("ZooKeeper")]
    [TestArea("Membership")]
    public sealed class ZooKeeperGatewayListProviderUnitTests
    {
        [Fact]
        public void Constructor_NullOptions_ThrowsArgumentNullException()
        {
            var exception = Assert.Throws<ArgumentNullException>(() =>
                new ZooKeeperGatewayListProvider(
                    NullLogger<ZooKeeperGatewayListProvider>.Instance,
                    null!,
                    CreateGatewayOptions(),
                    CreateClusterOptions()));

            Assert.Equal("options", exception.ParamName);
        }

        [Fact]
        public void Constructor_NullGatewayOptions_ThrowsArgumentNullException()
        {
            var exception = Assert.Throws<ArgumentNullException>(() =>
                new ZooKeeperGatewayListProvider(
                    NullLogger<ZooKeeperGatewayListProvider>.Instance,
                    CreateProviderOptions(),
                    null!,
                    CreateClusterOptions()));

            Assert.Equal("gatewayOptions", exception.ParamName);
        }

        [Fact]
        public void Constructor_NullClusterOptions_ThrowsArgumentNullException()
        {
            var exception = Assert.Throws<ArgumentNullException>(() =>
                new ZooKeeperGatewayListProvider(
                    NullLogger<ZooKeeperGatewayListProvider>.Instance,
                    CreateProviderOptions(),
                    CreateGatewayOptions(),
                    null!));

            Assert.Equal("clusterOptions", exception.ParamName);
        }

        [Theory]
        [InlineData("localhost:2181", "cluster-a", "localhost:2181/cluster-a")]
        [InlineData("localhost:2181/", "/cluster-a", "localhost:2181///cluster-a")]
        public void Constructor_ValidOptions_PreservesLiteralConnectionAndClusterPathComposition(
            string connectionString,
            string clusterId,
            string expectedDeploymentConnectionString)
        {
            var sut = CreateSut(connectionString, clusterId, TimeSpan.FromSeconds(37));

            Assert.Equal(expectedDeploymentConnectionString, GetPrivateField<string>(sut, "_deploymentConnectionString"));
            Assert.Equal("/" + clusterId, GetPrivateField<string>(sut, "_deploymentPath"));
        }

        [Fact]
        public void Constructor_ValidGatewayOptions_CopiesMaxStaleness()
        {
            var sut = CreateSut("sentinel.invalid:2181", "cluster-a", TimeSpan.FromSeconds(37));

            Assert.Equal(TimeSpan.FromSeconds(37), sut.MaxStaleness);
            Assert.True(sut.IsUpdatable);
        }

        private static ZooKeeperGatewayListProvider CreateSut(
            string connectionString,
            string clusterId,
            TimeSpan refreshPeriod) =>
            new(
                NullLogger<ZooKeeperGatewayListProvider>.Instance,
                CreateProviderOptions(connectionString),
                CreateGatewayOptions(refreshPeriod),
                CreateClusterOptions(clusterId));

        private static IOptions<ZooKeeperGatewayListProviderOptions> CreateProviderOptions(
            string connectionString = "sentinel.invalid:2181") =>
            Options.Create(new ZooKeeperGatewayListProviderOptions { ConnectionString = connectionString });

        private static IOptions<GatewayOptions> CreateGatewayOptions(TimeSpan? refreshPeriod = null) =>
            Options.Create(new GatewayOptions
            {
                GatewayListRefreshPeriod = refreshPeriod ?? TimeSpan.FromSeconds(37)
            });

        private static IOptions<ClusterOptions> CreateClusterOptions(string clusterId = "cluster-a") =>
            Options.Create(new ClusterOptions { ClusterId = clusterId });

        private static T GetPrivateField<T>(object instance, string fieldName)
        {
            var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return Assert.IsType<T>(field.GetValue(instance));
        }
    }
}
