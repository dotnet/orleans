namespace Orleans.Persistence.Cosmos.TypeInfo
{
    internal class FallbackGrainStateTypeInfoProvider : IGrainStateTypeInfoProvider
    {
        ILogger _logger;
        IList<IGrainStateTypeInfoProvider> _providers;

        public FallbackGrainStateTypeInfoProvider(
            ILogger logger,
            params IGrainStateTypeInfoProvider[] providers)
        {
            _logger = logger;
            _providers = providers;
        }

        GrainStateTypeInfo IGrainStateTypeInfoProvider.GetGrainStateTypeInfo(CosmosGrainStorage grainStorage, GrainReference grainReference, IGrainState grainState)
        {
            foreach (var provider in _providers)
            {
                try
                {
                    var result = provider.GetGrainStateTypeInfo(grainStorage, grainReference, grainState);
                    if (result is not null)
                    {
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug("Failed to resolve from provider: {exMessage}", ex);
                }
            }

            throw new InvalidOperationException($"Failed to resolve grain state type info for grain: '{grainReference}'. No provider was able to handle the request.");
        }
    }
}
