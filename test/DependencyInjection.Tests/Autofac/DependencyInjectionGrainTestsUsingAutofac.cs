using System.Threading.Tasks;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
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
            private class SiloBuilderConfiguratorConfiguringAutofac : IHostConfigurator
            {
                public void Configure(IHostBuilder hostBuilder)
                {
                    hostBuilder.UseServiceProviderFactory(new AutofacServiceProviderFactory());
                }
            }
        }

        public DependencyInjectionGrainTestsUsingAutofac(Fixture fixture)
            : base(fixture)
        {
        }
    }

    [TestCategory("DI"), TestCategory("Functional")]
    public class DependencyInjectionSiloStartsUsingAutofac : IClassFixture<DependencyInjectionSiloStartsUsingAutofac.Fixture>
    {
        private readonly BaseTestClusterFixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.GatewayPerSilo = false;
                builder.Options.InitialSilosCount = 2;
                builder.AddSiloBuilderConfigurator<HostBuilderConfigurator>();
            }

            //configure to use Autofac as DI container
            private class HostBuilderConfigurator : IHostConfigurator
            {
                public void Configure(IHostBuilder hostBuilder)
                {
                    hostBuilder.UseServiceProviderFactory(new AutofacServiceProviderFactory());
                }
            }

            private class SiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder siloBuilder)
                {
                }
            }
        }

        public DependencyInjectionSiloStartsUsingAutofac(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task ClusterStart()
        {
            var grain = this.fixture.Client.GetGrain<ISimpleGrain>(0);
            await grain.IncrementA();
        }
    }
}
