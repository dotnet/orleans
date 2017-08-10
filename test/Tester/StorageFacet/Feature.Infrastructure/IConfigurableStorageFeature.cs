
using Tester.StorageFacet.Abstractions;

namespace Tester.StorageFacet.Infrastructure
{
    public interface IConfigurableStorageFeature
    {
        void Configure(IStorageFeatureConfig config);
    }
}
