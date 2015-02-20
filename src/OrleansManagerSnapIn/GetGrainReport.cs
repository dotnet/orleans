using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management.Automation;
using System.Net;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using OrleansManager.Properties;
using Polly;

namespace OrleansManager
{
    [Cmdlet(VerbsCommon.Get, "GrainReport")]
    public sealed class GetGrainReport : Cmdlet
    {
        public GetGrainReport()
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

        /// <summary>
        /// The IPv4 or IPv6 address and the port of the gateway to connect to.
        /// </summary>
        /// <remarks>
        /// Use <see cref="System.Net.Dns.GetHostAddresses(string)"/> to resolve a hostname.
        /// </remarks>
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

        protected override void EndProcessing()
        {
            GrainClient.Uninitialize();
        }

        private void DoProcessRecord()
        {
            var grainId = OrleansManagerHelper.GetGrainIdOrWriteErrorAndReturnNull(InterfaceTypeCode, ImplementationClassName, GrainId);

            if (grainId == null)
            {
                return;
            }

            WriteVerbose(string.Format(CultureInfo.InvariantCulture, Resources.FullGrainId, grainId.ToFullString()));

            var siloAddresses = OrleansManagerHelper.GetSiloAddresses().ToArray();

            if (!siloAddresses.Any())
            {
                OrleansManagerHelper.WriteGatewayNotFoundError();
                return;
            }

            var detailedGrainReports = GetDetailedGrainReports(grainId, siloAddresses);

            WriteObject(detailedGrainReports, true);
            // do not call lookup grain implicitly
        }

        private IEnumerable<DetailedGrainReport> GetDetailedGrainReports(GrainId grainId, IEnumerable<SiloAddress> siloAddresses)
        {
            return siloAddresses
                .AsParallel()
                .Select(siloAddress => GetDetailedGrainReportWithPolicyAsync(grainId, siloAddress))
                .Select(task => task.Result);
        }

        private Task<DetailedGrainReport> GetDetailedGrainReportWithPolicyAsync(GrainId grainId, SiloAddress siloAddress)
        {
            WriteVerbose(string.Format(CultureInfo.CurrentCulture, Resources.CallingGetDetailedGrainReport, siloAddress, grainId));

            return Policy
                .Handle<Exception>()
                .Retry(3, OrleansManagerHelper.WriteGeneralError)
                .ExecuteAsync(() => GetDetailedGrainReportAsync(grainId, siloAddress));
        }

        private static Task<DetailedGrainReport> GetDetailedGrainReportAsync(GrainId grainId, SiloAddress siloAddress)
        {
            var siloControl = SiloControlFactory.GetSystemTarget(Constants.SiloControlId, siloAddress);
            return siloControl.GetDetailedGrainReport(grainId);
        }
    }
}