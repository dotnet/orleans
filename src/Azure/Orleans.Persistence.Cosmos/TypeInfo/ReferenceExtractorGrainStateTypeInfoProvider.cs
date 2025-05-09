using System.Collections.Concurrent;
using Orleans.Persistence.Migration;
using Orleans.Runtime;

namespace Orleans.Persistence.Cosmos.TypeInfo
{
    internal class ReferenceExtractorGrainStateTypeInfoProvider : BaseGrainStateTypeInfoProvider
    {
        private readonly IGrainReferenceExtractor grainReferenceExtractor;

        public ReferenceExtractorGrainStateTypeInfoProvider(IGrainReferenceExtractor grainReferenceExtractor, CosmosGrainStorageOptions options)
            : base(options)
        {
            this.grainReferenceExtractor = grainReferenceExtractor;
        }

        protected override (Type grainClass, string grainTypeName, Func<GrainReference, string> grainKeyFormatter) GetGrainTypeInfo(GrainReference grainReference)
        {
            // grainState.Type does not have a proper type -> we need to separately call extractor to find out a proper type
            var grainClass = grainReferenceExtractor.ExtractType(grainReference);

            var grainTypeName = GrainTypeResolver.GetGrainTypeByConvention(grainClass, forceGrainTypeAttribute: options?.ForceGrainTypeAttribute);
            var grainKeyFormatter = GrainStateTypeInfo.GetGrainKeyFormatter(grainClass);
            
            return (grainClass, grainTypeName, grainKeyFormatter);
        }
    }
}
