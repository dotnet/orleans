using Microsoft.Extensions.DependencyInjection;

namespace DependencyInjection.Tests.DefaultServiceProvider
{

    [TestCategory("DI"), TestCategory("Functional")]
    public class DependencyInjectionDisambiguationTestsUsingDefaultServiceProvider : DependencyInjectionDisambiguationTestRunner
    {
        protected override IServiceProvider BuildServiceProvider(IServiceCollection services)
        {
            return services.BuildServiceProvider();
        }
    }
}
