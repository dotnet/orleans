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
        private readonly CosmosGrainStorageOptions options;

        public ReferenceExtractorGrainStateTypeInfoProvider(
            IGrainReferenceExtractor grainReferenceExtractor,
            CosmosGrainStorageOptions options)
        {
            this.grainReferenceExtractor = grainReferenceExtractor;
            this.options = options;
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

                var overrideStateName = DetermineOverrideStateName(grainClass);

                grainStateTypeInfo
                    = this.grainStateTypeInfo[(typeCode, grainStateType)]
                    = new GrainStateTypeInfo(overrideStateName, grainTypeName, grainKeyFormatter, readStateFunc, writeStateFunc, clearStateFunc);
            }

            return grainStateTypeInfo;
        }

        /// <notes>
        /// Orleans 7.x+ have a hardcoded "state" value for Grain`T.
        /// Meaning for the migration tooling we need to find out if it is a Grain`T or not.
        ///<br/>
        /// See Grain`T: https://github.com/dotnet/orleans/blob/116da427e1a1e56477ef94f6058f1f5d1aec2f11/src/Orleans.Runtime/Core/GrainRuntime.cs#L86C9-L86C88 <br/>
        /// and PersistentState`T: https://github.com/dotnet/orleans/blob/116da427e1a1e56477ef94f6058f1f5d1aec2f11/src/Orleans.Runtime/Facet/Persistent/PersistentStateStorageFactory.cs#L31
        /// </notes>
        private string? DetermineOverrideStateName(Type grainClass)
        {
            if (options.UseLegacySerialization)
            {
                // override of stateName is only applicable for orleans7+ serialization
                return null;
            }

            var baseType = grainClass.BaseType;
            if (baseType is null)
            {
                return null;
            }

            if (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == typeof(Grain<>))
            {
                return "state";
            }

            return null;
        }
    }
}
