using Orleans.ApplicationParts;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans
{

    internal sealed class ClientLoggingHelper : BaseLoggingHelper<IClusterClientLifecycle>
    {
        public ClientLoggingHelper(GrainPropertiesResolver grainPropertiesResolver, IApplicationPartManager applicationPartManager)
            : base(grainPropertiesResolver, applicationPartManager)
        {
        }
    }
}
