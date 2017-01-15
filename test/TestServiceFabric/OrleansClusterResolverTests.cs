using System.Threading.Tasks;

using NSubstitute;
using Xunit;

namespace TestServiceFabric
{
    using System;
    using System.Fabric;
    using System.Fabric.Health;
    using System.Fabric.Query;

    using Microsoft.Orleans.ServiceFabric;
    using Microsoft.Orleans.ServiceFabric.Utilities;

    using Xunit.Abstractions;

    [TestCategory("ServiceFabric"), TestCategory("BVT")]
    public class OrleansClusterResolverTests
    {
        private readonly Uri serviceName;
        
        private readonly TestOutputLogger log;

        private readonly IFabricQueryManager queryManager;

        public OrleansClusterResolverTests(ITestOutputHelper output)
        {
            this.serviceName = new Uri("fabric:/test/robust/coffee");
            
            var first = new ServicePartitionList
            {
                new MockPartition(ServiceKind.Stateful, null, HealthState.Ok, ServicePartitionStatus.Ready)
            };

            this.queryManager = Substitute.For<IFabricQueryManager>();
            this.log = new TestOutputLogger(output);
        }

        [Fact]
        public async Task ClusterResolverBasicTest()
        {
            var resolver = new FabricServiceSiloResolver(
                this.serviceName,
                this.queryManager,
                this.log.GetLogger);
            await resolver.Refresh();
        }
    }

    public class MockPartition : Partition {
        public MockPartition(ServiceKind serviceKind, ServicePartitionInformation partitionInformation, HealthState healthState, ServicePartitionStatus partitionStatus)
            : base(serviceKind, partitionInformation, healthState, partitionStatus)
        {
        }
    }
}
