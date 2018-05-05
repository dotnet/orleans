using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace DependencyInjection.Tests.Autofac
{
    [TestCategory("DI"), TestCategory("Functional")]
    public class DependencyInjectionGrainTestsUsingAutofac : DependencyInjectionGrainTestRunner, IClassFixture<DependencyInjectionGrainTestsUsingAutofac.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 1;
                builder.AddSiloBuilderConfigurator<TestSiloBuilderConfigurator>();
                builder.AddSiloBuilderConfigurator<SiloBuilderConfiguratorConfiguringAutofac>();
            }
            //configure to use Autofac as DI container
            private class SiloBuilderConfiguratorConfiguringAutofac : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder.UseServiceProviderFactory(services =>
                    {
                        var containerBuilder = new ContainerBuilder();
                        containerBuilder.Populate(services);
                        return new AutofacServiceProvider(containerBuilder.Build());
                    });
                }
            }

        }

        public DependencyInjectionGrainTestsUsingAutofac(Fixture fixture)
            : base(fixture)
        {
        }
    }
}
