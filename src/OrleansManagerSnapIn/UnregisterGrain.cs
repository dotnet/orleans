using System;
using System.Globalization;
using System.Management.Automation;
using System.Net;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using OrleansManager.Properties;
using Polly;

namespace OrleansManager
{
    /// <summary>
    /// Unregister a <see cref="Orleans.Grain"/> from an Silo.
    /// </summary>
    [Cmdlet(VerbsCommon.Remove, "Grain")]
    public sealed class UnregisterGrain : Cmdlet
    {
        #region Parameters

        //TODO: provide info: how to get the type code
        /// <summary>
        /// The type code of the interface.
        /// </summary>
        [Parameter(ParameterSetName = "TypeCode", Position = 0, Mandatory = true)]
        public int InterfaceTypeCode { get; set; } = -1;

        //TODO: provide info: how to get the implementation class name
        /// <summary>
        /// The class name of the implementation.
        /// </summary>
        [Parameter(ParameterSetName = "ClassName", Position = 0, Mandatory = true)]
        public string ImplementationClassName { get; set; } = null;

        [Parameter(ParameterSetName = "TypeCode", Position = 1, Mandatory = true)]
        [Parameter(ParameterSetName = "ClassName", Position = 1, Mandatory = true)]
        public IPEndPoint Gateway { get; set; }

        /// <summary>
        /// The <see cref="Guid"/> or <see cref="long"/> value of the <see cref="Orleans.Runtime.GrainId"/>.
        /// </summary>
        [Parameter(ParameterSetName = "TypeCode", Position = 2, Mandatory = true)]
        [Parameter(ParameterSetName = "ClassName", Position = 2, Mandatory = true)]
        public string GrainId { get; set; } = string.Empty;
        
        /// <summary>
        /// Defines how often the delete should be retried.
        /// </summary>
        [Parameter(ParameterSetName = "TypeCode", Position = 3, Mandatory = false)]
        [Parameter(ParameterSetName = "ClassName", Position = 3, Mandatory = false)]
        public int MaxRetryAttempts { get; set; } = 3;

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

            var siloAddress = OrleansManagerHelper.GetSiloAddressOrNull();
            if (siloAddress == null)
            {
                OrleansManagerHelper.WriteGatewayNotFoundError();
                return;
            }

            var remoteGrainDirectory = RemoteGrainDirectoryFactory.GetSystemTarget(Constants.DirectoryServiceId, siloAddress);

            DeleteWithRetryPolicyAsync(grainId, remoteGrainDirectory).Wait();
        }

        private Task DeleteWithRetryPolicyAsync(GrainId grainId, IRemoteGrainDirectory remoteGrainDirectory)
        {
            return Policy
                .Handle<Exception>()
                .RetryAsync(MaxRetryAttempts, (exception, retryCount) =>
                {
                    var error = new ErrorRecord(exception, Resources.GeneralError, ErrorCategory.OperationStopped, remoteGrainDirectory);
                    WriteError(error);
                })
                .ExecuteAsync(() => remoteGrainDirectory.DeleteGrain(grainId))
                .ContinueWith(task => WriteVerbose(Resources.Done));
        }
        
    }
}