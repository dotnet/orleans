using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace Orleans.GrainDirectory
{
    /// <summary>
    /// Used to locate Grain activation in the cluster
    /// </summary>
    interface IGrainLocator
    {
        Task<ActivationAddress> Register(ActivationAddress address);

        Task Unregister(ActivationAddress address, UnregistrationCause cause);

        Task<List<ActivationAddress>> Lookup(GrainId grainId);

        bool TryLocalLookup(GrainId grainId, out List<ActivationAddress> addresses);
    }
}
