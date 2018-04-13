using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.Grains;

namespace DependencyInjection.Tests.AspnetDiContainer
{
    [TestCategory("DI"), TestCategory("BVT")]
    public class DependencyInjectionGrainTestsUsingASPNETDIContainer : DependencyInjectionGrainTestRunner
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 1;
                builder.AddSiloBuilderConfigurator<TestSiloBuilderConfigurator>();
                //Orleans would use ASP.NET DI container solution by default, so no need to configure ServiceProviderFactory here
            }


        }

        public DependencyInjectionGrainTestsUsingASPNETDIContainer()
            : base(new Fixture())
        {
        }
    }
}
