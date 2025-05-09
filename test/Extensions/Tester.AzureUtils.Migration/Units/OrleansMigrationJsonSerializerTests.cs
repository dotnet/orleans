using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Persistence.Migration;
using Orleans.Persistence.Migration.Serialization;
using Orleans.Runtime;
using Xunit;
using UniqueKey = Orleans.Runtime.UniqueKey;

namespace Tester.AzureUtils.Migration.Units
{
    public class OrleansMigrationJsonSerializerTests
    {
        private readonly OrleansMigrationJsonSerializer serializer;

        public OrleansMigrationJsonSerializerTests()
        {
            var silo = new Microsoft.Extensions.Hosting.HostBuilder()
                .UseOrleans((Microsoft.Extensions.Hosting.HostBuilderContext ctx, ISiloBuilder siloBuilder) =>
                {
                    siloBuilder
                    .Configure<ClusterOptions>(o => o.ClusterId = o.ServiceId = "s")
                    .AddMigrationTools()
                    .UseLocalhostClustering();
                })
                .Build();

            this.serializer = silo.Services.GetRequiredService<OrleansMigrationJsonSerializer>();
        }

        [Theory]
        [MemberData(nameof(TestIpEndpoints))]
        public void IpEndpoint_RoundtripSerialization(IPEndPoint endpoint)
        {
            var serialized = this.serializer.Serialize(endpoint, typeof(IPEndPoint));
            Assert.Contains("Address", serialized, StringComparison.InvariantCultureIgnoreCase);
            Assert.Contains("Port", serialized, StringComparison.InvariantCultureIgnoreCase);

            var deserialized = (IPEndPoint)this.serializer.Deserialize(typeof(IPEndPoint), serialized);
            Assert.Equal(endpoint.Port, deserialized.Port);
            Assert.Equal(endpoint.Address, deserialized.Address);
        }

        [Theory]
        [MemberData(nameof(TestMembershipVersions))]
        internal void MembershipVersion_RoundtripSerialization(MembershipVersion membershipVersion)
        {
            var serialized = this.serializer.Serialize(membershipVersion, typeof(MembershipVersion));
            Assert.NotEmpty(serialized);

            var deserialized = (MembershipVersion)this.serializer.Deserialize(typeof(MembershipVersion), serialized);
            Assert.Equal((long)membershipVersion, (long)deserialized);
        }

        [Theory]
        [MemberData(nameof(TestSiloAddresses))]
        internal void SiloAddress_RoundtripSerialization(SiloAddress siloAddress)
        {
            var serialized = this.serializer.Serialize(siloAddress, typeof(SiloAddress));
            Assert.NotEmpty(serialized);

            var deserialized = (SiloAddress)this.serializer.Deserialize(typeof(SiloAddress), serialized);
            Assert.Equal(siloAddress.ToLongString(), deserialized.ToLongString());
        }

        public static IEnumerable<object[]> TestIpEndpoints => new List<object[]>
        {
            new object[] { new IPEndPoint(IPAddress.Any, 12345) },
        };

        public static IEnumerable<object[]> TestMembershipVersions => new List<object[]>
        {
            new object[] { new MembershipVersion() },
            new object[] { new MembershipVersion(1342534) }
        };

        public static IEnumerable<object[]> TestSiloAddresses => new List<object[]>
        {
            new object[] { SiloAddress.New(new IPEndPoint(IPAddress.Any, port: 5000), gen: 3) },
            new object[] { SiloAddress.Zero }
        };
    }
}
