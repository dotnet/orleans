namespace Orleans.Persistence.Cosmos.TypeInfo
{
    internal interface IGrainStateTypeInfoProvider
    {
        GrainStateTypeInfo GetGrainStateTypeInfo(CosmosGrainStorage grainStorage, GrainReference grainReference, IGrainState grainState);
    }
}
