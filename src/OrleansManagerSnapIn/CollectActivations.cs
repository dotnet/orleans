using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Net;
using Orleans;
using Orleans.Runtime;
using OrleansManager.Properties;
using Polly;

namespace OrleansManager
{
    [Cmdlet("Collect", "Activations")]
    public sealed class CollectActivations : Cmdlet
    {
        public CollectActivations()
        {
            AgeLimit = TimeSpan.Zero;
        }

        #region Parameters

        [Parameter(Mandatory = true, HelpMessageResourceId = "SiloAddressesParameter", ValueFromPipeline = true)]
        public IEnumerable<string> SiloAddresses { get; set; }

        [Parameter(Mandatory = true)]
        public IPEndPoint Gateway { get; set; }

        [Parameter(Mandatory = false, HelpMessageResourceId = "AgeLimitParameter")]
        public TimeSpan AgeLimit { get; set; }

        #endregion
        
        private static IManagementGrain ManagementGrain
        {
            get { return OrleansManagerHelper.GetManagementGrain(); }
        }

        protected override void BeginProcessing()
        {
            OrleansManagerHelper.Initialize(WriteError, WriteDebug);
            GrainClient.Initialize(Gateway);
        }

        protected override void ProcessRecord()
        {
            Policy
                .Handle<Exception>()
                .Retry(0, OrleansManagerHelper.WriteGeneralError)
                .Execute(DoProcessRecord);
        }
        
        private void DoProcessRecord()
        {
            var siloAddresses = OrleansManagerHelper.ParseSiloAddresses(SiloAddresses);
            
            if (AgeLimit > TimeSpan.Zero)
            {
                WriteVerbose(Resources.ForcingActivationCollection);
                ManagementGrain.ForceActivationCollection(siloAddresses.ToArray(), AgeLimit);
            }
            else
            {
                WriteVerbose(Resources.ForcingGarbageCollection);
                ManagementGrain.ForceGarbageCollection(siloAddresses.ToArray());
            }

            WriteVerbose(Resources.Done);
        }

        protected override void EndProcessing()
        {
            GrainClient.Uninitialize();
        }
    }
}