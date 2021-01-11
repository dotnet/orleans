using System.Collections.Generic;
using Orleans.ApplicationParts;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Services;

namespace Orleans
{
    internal class SiloLoggingHelper : BaseLoggingHelper<ISiloLifecycle>
    {
        private readonly Dictionary<GrainType, string> grainServicesNames = new Dictionary<GrainType, string>();

        public SiloLoggingHelper(GrainPropertiesResolver grainPropertiesResolver, IApplicationPartManager applicationPartManager)
            : base(grainPropertiesResolver, applicationPartManager)
        {
        }

        public override string GetGrainTypeName(GrainType grainType)
        {
            if (grainType.IsGrainService())
            {
                if (this.grainServicesNames.TryGetValue(grainType, out var name))
                {
                    return name;
                }
            }

            // Base implementation will deal with SystemTargets and regular grains
            return base.GetGrainTypeName(grainType);
        }

        public void RegisterGrainService(IGrainService service)
        {
            var type = ((ISystemTargetBase)service).GrainId.Type;
            var name = service.GetType().FullName;
            this.grainServicesNames.Add(type, name);
        }
    }
}
