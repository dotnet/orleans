using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.ApplicationParts;
using Orleans.Metadata;
using Orleans.Runtime.Configuration;
using TestGrainInterfaces;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.ApplicationParts
{
    public class ApplicationPartTests
    {
        [Fact]
        public void T1()
        {
            var silo = new SiloHostBuilder()
                .UseConfiguration(ClusterConfiguration.LocalhostPrimarySilo())
                // DefaultCluster.dll (this)
                .AddApplicationPart(typeof(DefaultCluster.Tests.ActivationsLifeCycleTests.GrainActivateDeactivateTests).Assembly)
                // Tester.dll
                .AddApplicationPart(typeof(Tester.GrainServiceTests).Assembly)
                // TestGrainInterfaces.dll
                .AddApplicationPart(typeof(CircularStateTestState).Assembly)
                // TestGrains.dll
                .AddApplicationPart(typeof(TestGrains.AccountGrain).Assembly)
                // TestInternalGrains.dll
                .AddApplicationPart(typeof(TestInternalGrains.ProxyGrain).Assembly)
                .Build();

            var partManager = silo.Services.GetService<ApplicationPartManager>();
            partManager.AddFeatureProvider(new AssemblyAttributeFeatureProvider<GrainInterfaceFeature>());
            var feature = partManager.CreateAndPopulateFeature<GrainInterfaceFeature>();
            Assert.Contains(feature.Interfaces, metadata => metadata.InterfaceType == typeof(ISomeGrain)); 
        }
    }
}
