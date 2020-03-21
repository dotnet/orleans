using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace DependencyInjection.Tests.DefaultServiceProvider
{
    [TestCategory("DI"), TestCategory("Functional")]
    public class DependencyInjectionGrainTestsUsingDefaultServiceProvider : DependencyInjectionGrainTestRunner, IClassFixture<DependencyInjectionGrainTestsUsingDefaultServiceProvider.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 1;
                builder.AddSiloBuilderConfigurator<TestSiloBuilderConfigurator>();
            }
        }

        public DependencyInjectionGrainTestsUsingDefaultServiceProvider(Fixture fixture)
            : base(fixture)
        {
        }
    }
}
