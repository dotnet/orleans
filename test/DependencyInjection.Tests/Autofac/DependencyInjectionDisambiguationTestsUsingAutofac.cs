using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace DependencyInjection.Tests.Autofac
{

    [TestCategory("DI"), TestCategory("Functional")]
    public class DependencyInjectionDisambiguationTestsUsingAutofac : DependencyInjectionDisambiguationTestRunner
    {
        protected override IServiceProvider BuildServiceProvider(IServiceCollection services)
        {
            var containerBuilder = new ContainerBuilder();
            containerBuilder.Populate(services);
            return new AutofacServiceProvider(containerBuilder.Build());
        }
    }
}
