using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.ApplicationParts;
using Orleans.CodeGeneration;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans
{
    internal abstract class BaseLoggingHelper<T> : IGrainIdLoggingHelper, IInvokeMethodRequestLoggingHelper, ILifecycleParticipant<T>
        where T : ILifecycleObservable
    {
        protected readonly IRuntimeClient runtimeClient;
        protected readonly Dictionary<int, (string InterfaceName, Dictionary<int, string> Methods)> interfaceMethodMap
            = new Dictionary<int, (string Name, Dictionary<int, string> Methods)>();

        public BaseLoggingHelper(IRuntimeClient runtimeClient, IApplicationPartManager applicationPartManager)
        {
            this.runtimeClient = runtimeClient;

            var interfaceFeature = applicationPartManager.CreateAndPopulateFeature<GrainInterfaceFeature>();
            foreach (var grainInterface in interfaceFeature.Interfaces)
            {
                var interfaceType = grainInterface.InterfaceType;
                var interfaceId = GrainInterfaceUtils.GetGrainInterfaceId(interfaceType);
                var methodMap = new Dictionary<int, string>();
                foreach (var interfaceMethod in GrainInterfaceUtils.GetMethods(interfaceType))
                {
                    methodMap[GrainInterfaceUtils.ComputeMethodId(interfaceMethod)] = interfaceMethod.Name;
                }
                this.interfaceMethodMap[interfaceId] = (interfaceType.FullName, methodMap);
            }
        }

        public string GetGrainTypeName(int typeCode) => this.runtimeClient.GrainTypeResolver.GetGrainTypeName(typeCode);

        public abstract string GetSystemTargetName(GrainId grainId);

        public void GetInterfaceAndMethodName(int interfaceId, int methodId, out string interfaceName, out string methodName)
        {
            if (this.interfaceMethodMap.TryGetValue(interfaceId, out var interfaceInfo))
            {
                interfaceName = interfaceInfo.InterfaceName;
                if (!interfaceInfo.Methods.TryGetValue(methodId, out methodName))
                {
                    methodName = methodId.ToString();
                }
            }
            else
            {
                interfaceName = interfaceId.ToString();
                methodName = methodId.ToString();
            }
        }

        public void Participate(T lifecycle)
        {
            Task Setup(CancellationToken ct)
            {
                GrainId.GrainTypeNameMapper = this;
                InvokeMethodRequest.Helper = this;
                return Task.CompletedTask;
            }

            lifecycle.Subscribe<ClientLoggingHelper>(ServiceLifecycleStage.BecomeActive, Setup);
        }
    }
}
