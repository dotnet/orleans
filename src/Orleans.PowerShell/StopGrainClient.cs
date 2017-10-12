using Orleans;
using System.Management.Automation;

namespace OrleansPSUtils
{
    using System;

    [Cmdlet(VerbsLifecycle.Stop, "GrainClient")]
    public class StopGrainClient : PSCmdlet
    {
        [Parameter(Mandatory = false, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
        public IClusterClient Client { get; set; }

        protected override void ProcessRecord()
        {
            var client = this.Client ?? this.GetClient();
            if (client == null)
                throw new ArgumentException("No client specified.");

            this.CloseClient(client);
        }
    }
}
