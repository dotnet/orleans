using Microsoft.Extensions.DependencyInjection;
using StructureMap;

namespace DependencyInjection.Tests.StructureMap
{
    [TestCategory("DI"), TestCategory("Functional")]
    public class DependencyInjectionDisambiguationTestsUsingStructureMap : DependencyInjectionDisambiguationTestRunner
    {
        protected override IServiceProvider BuildServiceProvider(IServiceCollection services)
        {
            var ctr = new Container();
            ctr.Populate(services);
            return ctr.GetInstance<IServiceProvider>();
        }
    }
}
