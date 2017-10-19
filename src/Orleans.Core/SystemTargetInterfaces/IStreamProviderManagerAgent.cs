using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    /// <summary>
    /// IStreamProviderManagerAgent interface that defines interface for runtime adding/removing stream providers.
    /// </summary>
    internal interface IStreamProviderManagerAgent : ISystemTarget
    {
        Task UpdateStreamProviders(IDictionary<string, ProviderCategoryConfiguration> streamProviderConfigurations);
    }
}
