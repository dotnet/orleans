using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.GrainDirectory
{
    /// <summary>
    /// Used to locate Grain activation in the cluster
    /// </summary>
    public interface IGrainLocator
    {
        Task<ActivationAddress> Register(ActivationAddress address);

        Task Unregister(ActivationAddress address, UnregistrationCause cause);

        ValueTask<ActivationAddress> Lookup(GrainId grainId);

        bool TryLocalLookup(GrainId grainId, out ActivationAddress addresses);
    }
}
