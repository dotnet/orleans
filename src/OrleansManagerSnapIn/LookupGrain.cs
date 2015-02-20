using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management.Automation;
using System.Net;
using Orleans;
using Orleans.Runtime;
using OrleansManager.Properties;
using Polly;

namespace OrleansManager
{
    [Cmdlet(VerbsCommon.Find, "Grain")]
    public sealed class LookupGrain : Cmdlet
    {
        public LookupGrain()
        {
            InterfaceTypeCode = -1;
        }

        #region Parameters

        //TODO: provide info: how to get the type code
        /// <summary>
        /// The type code of the interface.
        /// </summary>
        [Parameter(ParameterSetName = "TypeCode", Position = 0, Mandatory = true)]
        public int InterfaceTypeCode { get; set; } 

        //TODO: provide info: how to get the implementation class name
        /// <summary>
        /// The class name of the implementation.
        /// </summary>
        [Parameter(ParameterSetName = "ClassName", Position = 0, Mandatory = true)]
        public string ImplementationClassName { get; set; }

        [Parameter(ParameterSetName = "TypeCode", Position = 1, Mandatory = true)]
        [Parameter(ParameterSetName = "ClassName", Position = 1, Mandatory = true)]
        public IPEndPoint Gateway { get; set; }

        /// <summary>
        /// The <see cref="Guid"/> or <see cref="long"/> value of the <see cref="Orleans.Runtime.GrainId"/>.
        /// </summary>
        [Parameter(ParameterSetName = "TypeCode", Position = 2, Mandatory = true)]
        [Parameter(ParameterSetName = "ClassName", Position = 2, Mandatory = true)]
        public string GrainId { get; set; }
        
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
            var grainId = OrleansManagerHelper.GetGrainIdOrWriteErrorAndReturnNull(InterfaceTypeCode, ImplementationClassName, GrainId);
            if (grainId == null)
            {
                return;
            }

            var siloAddress = OrleansManagerHelper.GetSiloAddressOrNull();
            if (siloAddress == null)
            {
                OrleansManagerHelper.WriteGatewayNotFoundError();
                return;
            }

            var directory = RemoteGrainDirectoryFactory.GetSystemTarget(Constants.DirectoryServiceId, siloAddress);

            WriteDebug(string.Format(CultureInfo.CurrentCulture, Resources.GrainInSiloLookup, siloAddress, grainId));
            var lookupResult = directory.LookUp(grainId).Result.Item1 ?? new List<Tuple<SiloAddress, ActivationId>>();

            var grainLookupResults = lookupResult.Select(tuple => new GrainLookupResult
            { 
                ActivationId = tuple.Item2.ToString(),
                SiloAddress = tuple.Item1.ToString()
            });

            WriteObject(grainLookupResults, true);
        }

        protected override void EndProcessing()
        {
            GrainClient.Uninitialize();
        }
    }
}