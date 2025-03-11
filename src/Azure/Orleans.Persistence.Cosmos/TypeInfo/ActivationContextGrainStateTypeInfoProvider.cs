using System.Collections.Concurrent;
using System.Reflection;

namespace Orleans.Persistence.Cosmos.TypeInfo
{
    internal class ActivationContextGrainStateTypeInfoProvider : IGrainStateTypeInfoProvider
    {
        private readonly ConcurrentDictionary<(ulong grainTypeCode, Type stateType), GrainStateTypeInfo> grainStateTypeInfo = new();
        private readonly IGrainActivationContextAccessor contextAccessor;

        public ActivationContextGrainStateTypeInfoProvider(IGrainActivationContextAccessor contextAccessor)
        {
            this.contextAccessor = contextAccessor;
        }

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
                var grainContext = this.contextAccessor.GrainActivationContext;
                if (grainContext is null)
                {
                    throw new InvalidOperationException($"'{nameof(IGrainActivationContextAccessor)}.{nameof(IGrainActivationContextAccessor.GrainActivationContext)}' is not initialized. This likely indicates a concurrency issue, such as attempting to access storage from a non-grain thread.");
                }

                var grainClass = grainContext.GrainType;
                var grainTypeAttr = grainClass.GetCustomAttribute<GrainTypeAttribute>();
                if (grainTypeAttr is null)
                {
                    throw new InvalidOperationException($"All grain classes must specify a grain type name using the [GrainType(type)] attribute. Grain class '{grainClass}' does not.");
                }

                // Work out how to format the grain id.
                var grainTypeName = grainTypeAttr.GrainType;
                var grainKeyFormatter = GrainStateTypeInfo.GetGrainKeyFormatter(grainClass);

                // Create methods for reading/writing/clearing the state based on the grain state type.
                var readStateAsync = CosmosGrainStorage.ReadStateAsyncCoreMethodInfo.MakeGenericMethod(grainStateType).CreateDelegate<Func<string, GrainId, IGrainState, Task>>(grainStorage);
                var writeStateAsync = CosmosGrainStorage.WriteStateAsyncCoreMethodInfo.MakeGenericMethod(grainStateType).CreateDelegate<Func<string, GrainId, IGrainState, Task>>(grainStorage);
                var clearStateAsync = CosmosGrainStorage.ClearStateAsyncCoreMethodInfo.MakeGenericMethod(grainStateType).CreateDelegate<Func<string, GrainId, IGrainState, Task>>(grainStorage);

                grainStateTypeInfo = this.grainStateTypeInfo[(typeCode, grainStateType)] = new(grainTypeName, grainKeyFormatter, readStateAsync, writeStateAsync, clearStateAsync);
            }

            return grainStateTypeInfo;
        }
    }
}
