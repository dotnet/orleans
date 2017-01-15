using Orleans;
using System.Management.Automation;

namespace OrleansPSUtils
{
    [Cmdlet(VerbsLifecycle.Stop, "GrainClient")]
    public class StopGrainClient : Cmdlet
    {
        protected override void ProcessRecord()
        {
            if (GrainClient.IsInitialized)
                GrainClient.Uninitialize();
        }
    }
}
