using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using Orleans.Persistence.Cosmos.TypeInfo;
using Orleans.Persistence.Migration;
using Orleans.Runtime;

namespace Orleans.Persistence.Cosmos.Migration
{
    internal class ReferenceExtractorGrainStateTypeInfoProvider : IGrainStateTypeInfoProvider
    {
        private readonly IGrainReferenceExtractor grainReferenceExtractor;

        public ReferenceExtractorGrainStateTypeInfoProvider(IGrainReferenceExtractor grainReferenceExtractor)
        {
            this.grainReferenceExtractor = grainReferenceExtractor;
        }

        private readonly ConcurrentDictionary<(ulong grainTypeCode, Type stateType), GrainStateTypeInfo> grainStateTypeInfo = new();

        public GrainStateTypeInfo GetGrainStateTypeInfo(CosmosGrainStorage grainStorage, GrainReference grainReference, IGrainState grainState)
        {
            var keyInfo = grainReference.ToKeyInfo();
            var (_, _, typeCode, _) = keyInfo.Key;
            if (!string.IsNullOrEmpty((string?)keyInfo.GenericArgument))
            {
                throw new InvalidOperationException($"Generic grain types are not supported by this provider. Grain: '{grainReference}'.");
            }

            var grainStateType = grainState.Type;
            if (!this.grainStateTypeInfo.TryGetValue((typeCode, grainStateType), out var grainStateTypeInfo))
            {
                // grainState.Type does not have a proper type -> we need to separately call extractor to find out a proper type
                var grainClass = grainReferenceExtractor.ExtractType(grainReference);

                var grainTypeAttr = grainClass.GetCustomAttribute<GrainTypeAttribute>();
                if (grainTypeAttr is null)
                {
                    throw new InvalidOperationException($"All grain classes must specify a grain type name using the [GrainType(type)] attribute. Grain class '{grainClass}' does not.");
                }
                var grainTypeName = grainTypeAttr.GrainType;
                var grainKeyFormatter = GrainStateTypeInfo.GetGrainKeyFormatter(grainClass);

                var readStateFunc = CosmosGrainStorage.ReadStateAsyncCoreMethodInfo.MakeGenericMethod(grainStateType).CreateDelegate<Func<string, GrainId, IGrainState, Task>>(grainStorage);
                var writeStateFunc = CosmosGrainStorage.WriteStateAsyncCoreMethodInfo.MakeGenericMethod(grainStateType).CreateDelegate<Func<string, GrainId, IGrainState, Task>>(grainStorage);
                var clearStateFunc = CosmosGrainStorage.ClearStateAsyncCoreMethodInfo.MakeGenericMethod(grainStateType).CreateDelegate<Func<string, GrainId, IGrainState, Task>>(grainStorage);

                grainStateTypeInfo = this.grainStateTypeInfo[(typeCode, grainStateType)] = new GrainStateTypeInfo(grainTypeName, grainKeyFormatter, readStateFunc, writeStateFunc, clearStateFunc);
            }

            return grainStateTypeInfo;
        }
    }
}
