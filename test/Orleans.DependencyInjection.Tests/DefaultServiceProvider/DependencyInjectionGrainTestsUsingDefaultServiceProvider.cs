using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace DependencyInjection.Tests.DefaultServiceProvider
{
    /// <summary>
    /// Tests dependency injection functionality using the default Microsoft DI container.
    /// Inherits all test cases from DependencyInjectionGrainTestRunner to verify
    /// that the default ServiceProvider implementation works correctly with Orleans.
    /// </summary>
    [TestCategory("DI"), TestCategory("Functional")]
    public class DependencyInjectionGrainTestsUsingDefaultServiceProvider : DependencyInjectionGrainTestRunner, IClassFixture<DependencyInjectionGrainTestsUsingDefaultServiceProvider.Fixture>
    {
        /// <summary>
        /// Test fixture that configures a single-silo cluster with the default DI container.
        /// </summary>
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
