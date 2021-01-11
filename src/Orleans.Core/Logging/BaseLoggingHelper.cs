using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.ApplicationParts;
using Orleans.CodeGeneration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Utilities;

namespace Orleans
{
    internal abstract class BaseLoggingHelper<T> : IGrainIdLoggingHelper, IInvokeMethodRequestLoggingHelper, ILifecycleParticipant<T>
        where T : ILifecycleObservable
    {
        protected readonly GrainPropertiesResolver grainPropertiesResolver;

        protected readonly CachedReadConcurrentDictionary<GrainType, string> grainTypeNameCache = new CachedReadConcurrentDictionary<GrainType, string>();

        protected readonly Dictionary<int, (string InterfaceName, Dictionary<int, string> Methods)> interfaceMethodMap
            = new Dictionary<int, (string Name, Dictionary<int, string> Methods)>();

        public BaseLoggingHelper(GrainPropertiesResolver grainPropertiesResolver, IApplicationPartManager applicationPartManager)
        {
            this.grainPropertiesResolver = grainPropertiesResolver;

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

        public virtual string GetGrainTypeName(GrainType grainType)
        {
            if (grainType.IsLegacyGrain())
            {
                if (this.grainTypeNameCache.TryGetValue(grainType, out var name))
                {
                    return name;
                }

                var grainProperties = this.grainPropertiesResolver.GetGrainProperties(grainType);
                foreach (var property in grainProperties.Properties)
                {
                    if (property.Key.Equals(WellKnownGrainTypeProperties.FullTypeName))
                    {
                        this.grainTypeNameCache.TryAdd(grainType, property.Value);
                        return property.Value;
                    }
                }
            }

            return grainType.ToStringUtf8();
        }

        public void GetInterfaceAndMethodName(int interfaceTypeCode, int methodId, out string interfaceName, out string methodName)
        {
            if (this.interfaceMethodMap.TryGetValue(interfaceTypeCode, out var interfaceInfo))
            {
                interfaceName = interfaceInfo.InterfaceName;
                if (!interfaceInfo.Methods.TryGetValue(methodId, out methodName))
                {
                    methodName = methodId.ToString();
                }
            }
            else
            {
                interfaceName = interfaceTypeCode.ToString();
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
