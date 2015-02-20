using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using Orleans;
using Orleans.Runtime;
using OrleansManager.Properties;

namespace OrleansManager
{
    /// <summary>
    /// Contains shared functionality.
    /// </summary>
    internal static class OrleansManagerHelper
    {
        private static Action<ErrorRecord> WriteError { get; set; }
        private static Action<string> WriteDebug { get; set; }

        public static void Initialize(Action<ErrorRecord> writeError, Action<string> writeDebug)
        {
            WriteError = writeError;
            WriteDebug = writeDebug;
        }

        public static IEnumerable<SiloAddress> ParseSiloAddresses(IEnumerable<string> siloAddresses)
        {
            WriteDebug(Resources.ParsingSiloAddresses);
            return siloAddresses.Select(SiloAddress.FromParsableString);
        }

        private static readonly Lazy<IManagementGrain> ManagementGrain = new Lazy<IManagementGrain>(() =>
        {
            WriteDebug(Resources.GettingManagementGrain);
            return ManagementGrainFactory.GetGrain(RuntimeInterfaceConstants.SystemManagementId);
        });

        public static IManagementGrain GetManagementGrain()
        {
            return ManagementGrain.Value;
        }
        
        public static void WriteGeneralError(Exception exception, int retryCount)
        {
            var error = new ErrorRecord(exception, Resources.GeneralError, ErrorCategory.NotSpecified, null);
            WriteError(error);
        }

        public static GrainId GetGrainIdOrWriteErrorAndReturnNull(int interfaceTypeCode, string implementationClassName, string grainIdString)
        {
            long implementationTypeCode;
            if (!TryGetImplementationTypeCode(interfaceTypeCode, implementationClassName, out implementationTypeCode))
            {
                WriteCantRetriveImplementationTypeCodeError();
                return null;
            }

            GrainId grainId;
            if (!TryGetGrainId(implementationTypeCode, grainIdString, out grainId))
            {
                WriteCantRetriveGrainIdError();
                return null;
            }

            return grainId;
        }

        private static void WriteCantRetriveGrainIdError()
        {
            var exception = new ArgumentException(Resources.GrainIdRequired);
            var error = new ErrorRecord(exception, Resources.GrainIdUnknown, ErrorCategory.InvalidArgument, null);
            WriteError(error);
        }

        private static void WriteCantRetriveImplementationTypeCodeError()
        {
            var exception = new ArgumentException(Resources.TypeCodeRequired);
            var error = new ErrorRecord(exception, Resources.TypeCodeUnknown, ErrorCategory.InvalidArgument, null);
            WriteError(error);
        }

        private static bool TryGetImplementationTypeCode(int interfaceTypeCode, string implementationClassName, out long implementationTypeCode)
        {
            if (IsInterfaceTypeCodeSet(interfaceTypeCode))
            {
                implementationTypeCode = TypeCodeMapper.GetImplementationTypeCode(interfaceTypeCode);
                return true;
            }

            if (IsImplementationClassNameSet(implementationClassName))
            {
                implementationTypeCode = TypeCodeMapper.GetImplementationTypeCode(implementationClassName);
                return true;
            }

            implementationTypeCode = -1L;
            return false;
        }

        private static bool IsImplementationClassNameSet(string implementationClassName)
        {
            return !String.IsNullOrWhiteSpace(implementationClassName);
        }

        private static bool IsInterfaceTypeCodeSet(int interfaceTypeCode)
        {
            return interfaceTypeCode >= 0;
        }

        private static bool TryGetGrainId(long implementationTypeCode, string grainIdString, out GrainId grainId)
        {
            long longGrainId;
            if (long.TryParse(grainIdString, out longGrainId))
            {
                grainId = GrainId.GetGrainId(implementationTypeCode, longGrainId);
                return true;
            }

            Guid guidGrainId;
            if (Guid.TryParse(grainIdString, out guidGrainId))
            {
                grainId = GrainId.GetGrainId(implementationTypeCode, guidGrainId);
                return true;
            }

            grainId = null;
            return false;
        }

        public static SiloAddress GetSiloAddressOrNull()
        {
            return GetSiloAddresses().FirstOrDefault();
        }

        public static IEnumerable<SiloAddress> GetSiloAddresses()
        {
            return GrainClient.Gateways.Select(Utils.ToSiloAddress);
        }

        public static void WriteGatewayNotFoundError()
        {
            var exception = new GatewayNotFoundException();
            var error = new ErrorRecord(exception, Resources.GeneralError, ErrorCategory.ObjectNotFound, null);
            WriteError(error);
        }
    }
}
