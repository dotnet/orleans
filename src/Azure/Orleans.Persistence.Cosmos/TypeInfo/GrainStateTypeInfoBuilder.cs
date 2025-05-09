using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Persistence.Migration;

namespace Orleans.Persistence.Cosmos.TypeInfo
{
    internal static class GrainStateTypeInfoBuilder
    {
        public static ActivationContextGrainStateTypeInfoProvider BuildActivationContextGrainStateTypeInfoProvider(IServiceProvider services, string name)
        {
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<CosmosGrainStorageOptions>>();
            var options = optionsMonitor.Get(name);

            var grainActivationContextAccessor = services.GetRequiredService<IGrainActivationContextAccessor>();
            return new ActivationContextGrainStateTypeInfoProvider(grainActivationContextAccessor, options);
        }

        public static ReferenceExtractorGrainStateTypeInfoProvider BuildReferenceExtractorGrainStateTypeInfoProvider(IServiceProvider services, string name)
        {
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<CosmosGrainStorageOptions>>();
            var options = optionsMonitor.Get(name);

            var grainReferenceExtractor = services.GetRequiredService<IGrainReferenceExtractor>();
            return new ReferenceExtractorGrainStateTypeInfoProvider(grainReferenceExtractor, options);
        }

        public static FallbackGrainStateTypeInfoProvider BuildFallbackGrainStateTypeInfoProvider(IServiceProvider services, string name)
        {
            var activationContextProvider = BuildActivationContextGrainStateTypeInfoProvider(services, name);
            var referenceExtractorProvider = BuildReferenceExtractorGrainStateTypeInfoProvider(services, name);

            var loggerFactory = services.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<FallbackGrainStateTypeInfoProvider>();

            return new FallbackGrainStateTypeInfoProvider(
                logger,
                // order is important here: providers are used in the order they are defined here
                activationContextProvider,
                referenceExtractorProvider);
        }
    }
}
