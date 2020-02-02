using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;
using StructureMap;
using TestExtensions;
using Xunit;

namespace DependencyInjection.Tests.StructureMap
{
    [TestCategory("DI"), TestCategory("Functional")]
    public class DependencyInjectionGrainTestsUsingStructureMap : DependencyInjectionGrainTestRunner, IClassFixture<DependencyInjectionGrainTestsUsingStructureMap.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 1;
                builder.AddSiloBuilderConfigurator<TestSiloBuilderConfigurator>();
                builder.AddSiloBuilderConfigurator<SiloBuilderConfiguratorConfiguringStructureMap>();
            }

            //configure to use StructureMap as DI container
            private class SiloBuilderConfiguratorConfiguringStructureMap : IHostConfigurator
            {
                public void Configure(IHostBuilder hostBuilder)
                {
                    hostBuilder.UseServiceProviderFactory(new StructureMapServiceProviderFactory(new Registry()));
                }
            }
        }

        public DependencyInjectionGrainTestsUsingStructureMap(Fixture fixture)
            : base(fixture)
        {
        }
    }
}
