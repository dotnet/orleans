using System.Collections.Concurrent;
using Orleans.Runtime;

namespace Orleans.Persistence.Cosmos.TypeInfo
{
    internal abstract class BaseGrainStateTypeInfoProvider : IGrainStateTypeInfoProvider
    {
        protected readonly CosmosGrainStorageOptions options;
        private readonly ConcurrentDictionary<(ulong grainTypeCode, Type stateType), GrainStateTypeInfo> grainStateTypeInfo = new();

        protected BaseGrainStateTypeInfoProvider(CosmosGrainStorageOptions options)
        {
            this.options = options;
        }

        protected abstract (Type grainClass, string grainTypeName, Func<GrainReference, string> grainKeyFormatter) GetGrainTypeInfo(GrainReference grainReference);

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
                var (grainClass, grainTypeName, grainKeyFormatter) = GetGrainTypeInfo(grainReference);

                // Create methods for reading/writing/clearing the state based on the grain state type.
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
#pragma warning disable OrleansCosmosExperimental
            if (options.UseExperimentalFormat)
#pragma warning restore OrleansCosmosExperimental
            {
                // override of stateName is only applicable for orleans7+ serialization
                return null;
            }

            var baseType = grainClass.BaseType;
            while (baseType != null)
            {
                if (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == typeof(Grain<>))
                {
                    return "state";
                }

                baseType = baseType.BaseType;
            }

            return null;
        }
    }
}
