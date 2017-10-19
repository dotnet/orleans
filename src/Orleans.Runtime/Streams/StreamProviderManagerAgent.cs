using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Streams;

namespace Orleans.Runtime
{
    /// <summary>
    /// System target that specifically handles runtime adding/removing stream providers.
    /// </summary>
    internal class StreamProviderManagerAgent : SystemTarget, IStreamProviderManagerAgent
    {
        private readonly StreamProviderManager streamProviderManager;
        private readonly IStreamProviderRuntime streamProviderRuntime;
        private readonly IDictionary<string, ProviderCategoryConfiguration> providerConfigurations;
        private readonly ILogger logger;
        private readonly AsyncSerialExecutor nonReentrancyGuarantor;
        public StreamProviderManagerAgent(Silo silo, IStreamProviderRuntime streamProviderRuntime, ILoggerFactory loggerFactory)
            : base(Constants.StreamProviderManagerAgentSystemTargetId, silo.SiloAddress, loggerFactory)

        {
            logger = loggerFactory.CreateLogger<StreamProviderManagerAgent>();
            this.streamProviderManager = (StreamProviderManager)silo.StreamProviderManager;
            providerConfigurations = silo.GlobalConfig.ProviderConfigurations;
            this.streamProviderRuntime = streamProviderRuntime;
            nonReentrancyGuarantor = new AsyncSerialExecutor();
        }

        public async Task UpdateStreamProviders(IDictionary<string, ProviderCategoryConfiguration> streamProviderConfigurations)
        {
            // Put the call into async serial executor so they will be executed in sequence.
            await nonReentrancyGuarantor.AddNext(() => Update(streamProviderConfigurations));
        }

        private async Task Update(IDictionary<string, ProviderCategoryConfiguration> streamProviderConfigurations)
        {
            ProviderCategoryConfiguration categoryConfig;
            streamProviderConfigurations.TryGetValue(ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME, out categoryConfig);
            if (categoryConfig == null)
            {
                if (logger.IsEnabled(LogLevel.Debug)) { logger.Debug("streamProviderConfigurations does not contain '" + ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME 
                    + "' element. Nothing to update."); }
                return;
            }

            IList<string> addList = new List<string>();
            IList<string> removeList = new List<string>();

            var siloStreamProviderManager = streamProviderManager;

            var existingProviders = siloStreamProviderManager.GetStreamProviders().Select(p => ((IProvider)p).Name).ToList();
            var newProviderList = categoryConfig.Providers;
            foreach (var providerName in existingProviders)
            {
                if (!newProviderList.ContainsKey(providerName))
                {
                    removeList.Add(providerName);
                    if (logger.IsEnabled(LogLevel.Debug)) { logger.Debug("Removing stream provider '" + providerName + "' from silo"); }
                }
            }
            foreach (var providerName in newProviderList.Keys)
            {
                if (!existingProviders.Contains(providerName))
                {
                    addList.Add(providerName);
                    if (logger.IsEnabled(LogLevel.Debug)) { logger.Debug("Adding stream provider '" + providerName + "' to silo"); }
                }
            }
            try
            {
                // Removing providers from silo first
                await siloStreamProviderManager.RemoveProviders(removeList);

                // Adding new providers to silo
                await siloStreamProviderManager.LoadStreamProviders(streamProviderConfigurations, this.streamProviderRuntime);

                // Starting new providers
                await siloStreamProviderManager.StartStreamProviders(addList);
            }
            catch (ProviderStartException exc)
            {
                logger.Error(ErrorCode.Provider_ErrorFromInit, exc.Message, exc);
                throw;
            }
            catch (ProviderInitializationException exc)
            {
                logger.Error(ErrorCode.Provider_ErrorFromInit, exc.Message, exc);
                throw;
            }

            if (logger.IsEnabled(LogLevel.Debug)) { logger.Debug("Stream providers updated successfully."); }

            providerConfigurations[ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME] = 
                streamProviderConfigurations[ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME];
        }
    }
}
