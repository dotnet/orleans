using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Net;
using Orleans;
using OrleansManager.Properties;
using Polly;

namespace OrleansManager
{
    [Cmdlet(VerbsCommon.Get, "GrainStatistics")]
    public sealed class GetGrainStatistics : Cmdlet
    {
        #region Parameters

        [Parameter(Mandatory = true, HelpMessageResourceId = "SiloAdressesParameter", Position = 0, ValueFromPipeline = true)]
        public IEnumerable<string> SiloAddresses { get; set; }
        
        [Parameter(Mandatory = true)]
        public IPEndPoint Gateway { get; set; }

        #endregion

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
            
            WriteVerbose(Resources.GettingStatistics);
            var stats = OrleansManagerHelper.GetManagementGrain().GetSimpleGrainStatistics(siloAddresses.ToArray()).Result;

            WriteObject(stats, true);
        }

        protected override void EndProcessing()
        {
            GrainClient.Uninitialize();
        }
    }
}