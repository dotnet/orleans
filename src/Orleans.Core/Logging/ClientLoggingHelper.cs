using Orleans.ApplicationParts;
using Orleans.Runtime;

namespace Orleans
{

    internal sealed class ClientLoggingHelper : BaseLoggingHelper<IClusterClientLifecycle>
    {
        public ClientLoggingHelper(IRuntimeClient runtimeClient, IApplicationPartManager applicationPartManager)
            : base(runtimeClient, applicationPartManager)
        {
        }

        public override string GetSystemTargetName(GrainId grainId) => Constants.SystemTargetName(grainId);
    }
}
