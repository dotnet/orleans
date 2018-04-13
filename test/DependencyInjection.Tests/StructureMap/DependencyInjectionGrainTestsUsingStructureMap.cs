using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Hosting;
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
            private class SiloBuilderConfiguratorConfiguringStructureMap : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder.UseServiceProviderFactory(services =>
                    {
                        var ctr = new Container();
                        ctr.Populate(services);
                        return ctr.GetInstance<IServiceProvider>();
                    });
                }
            }

        }

        public DependencyInjectionGrainTestsUsingStructureMap(Fixture fixture)
            : base(fixture)
        {
        }
    }
}
