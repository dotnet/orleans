using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    internal static class GrainLocatorFactory
    {
        public static IGrainLocator GetGrainLocator(IServiceProvider sp)
        {
            var customDirectory = sp.GetService<IGrainDirectory>();
            var inClusterGrainLocator = new DhtGrainLocator(sp.GetRequiredService<ILocalGrainDirectory>());

            return customDirectory != null
                ? new GrainLocator(customDirectory, inClusterGrainLocator)
                : (IGrainLocator) inClusterGrainLocator;
        }
    }
}
