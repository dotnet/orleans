using System.Collections.Concurrent;

namespace Orleans.Persistence.Cosmos.TypeInfo
{
    internal class ActivationContextGrainStateTypeInfoProvider : BaseGrainStateTypeInfoProvider
    {
        private readonly ConcurrentDictionary<(ulong grainTypeCode, Type stateType), GrainStateTypeInfo> grainStateTypeInfo = new();
        private readonly IGrainActivationContextAccessor contextAccessor;

        public ActivationContextGrainStateTypeInfoProvider(
            IGrainActivationContextAccessor contextAccessor,
            CosmosGrainStorageOptions options)
            : base(options)
        {
            this.contextAccessor = contextAccessor;
        }

        protected override (Type grainClass, string grainTypeName, Func<GrainReference, string> grainKeyFormatter) GetGrainTypeInfo(GrainReference grainReference)
        {
            var grainContext = this.contextAccessor.GrainActivationContext;
            if (grainContext is null)
            {
                throw new InvalidOperationException($"'{nameof(IGrainActivationContextAccessor)}.{nameof(IGrainActivationContextAccessor.GrainActivationContext)}' is not initialized. This likely indicates a concurrency issue, such as attempting to access storage from a non-grain thread.");
            }

            var grainClass = grainContext.GrainType;
            var grainTypeName = GrainTypeResolver.GetGrainTypeByConvention(grainClass, forceGrainTypeAttribute: options?.ForceGrainTypeAttribute);
            var grainKeyFormatter = GrainStateTypeInfo.GetGrainKeyFormatter(grainClass);

            return (grainClass, grainTypeName, grainKeyFormatter);
        }
    }
}
