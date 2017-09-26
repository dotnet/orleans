﻿using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
        private readonly ITestOutputHelper output;
        public OrleansClusterResolverTests(ITestOutputHelper output)
        {
            this.serviceName = new Uri("fabric:/test/robust/coffee");
            
            var first = new ServicePartitionList
            {
                new MockPartition(ServiceKind.Stateful, null, HealthState.Ok, ServicePartitionStatus.Ready)
            };

            this.queryManager = Substitute.For<IFabricQueryManager>();
            this.log = new TestOutputLogger(output);
            this.output = output;
        }

        [Fact]
        public async Task ClusterResolverBasicTest()
        {
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(new TestOutputLoggerProvider(this.output));
            var resolver = new FabricServiceSiloResolver(
                this.serviceName,
                this.queryManager,
                loggerFactory);
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
