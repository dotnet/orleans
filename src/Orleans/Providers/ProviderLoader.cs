using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Providers
{
    /// <summary>
    /// Providers configuration and loading error semantics:
    /// 1) We will only load the providers that were specified in the config. 
    /// If a provider is not specified in the config, we will not attempt to load it.
    /// Specificaly, it means both storage and streaming providers are loaded only if configured.
    /// 2) If a provider is specified in the config, but was not loaded (no type found, or constructor failed, or Init failed), the silo will fail to start.
    /// 
    /// Loading providers workflow and error handling implementation:
    /// 1) Load ProviderCategoryConfiguration.
    ///     a) If CategoryConfiguration not found - it is not an error, continue.
    /// 2) Go over all assemblies and load all found providers and instantiate them via ProviderTypeManager.
    ///     a) If a certain found provider type failed to get instantiated, it is not an error, continue.
    /// 3) Validate all providers were loaded: go over all provider config and check that we could indeed load and instantiate all of them.
    ///     a) If failed to load or instantiate at least one configured provider, fail the silo start.
    /// 4) InitProviders: call Init on all loaded providers. 
    ///     a) Failure to init a provider wil result in silo failing to start.
    /// </summary>
    /// <typeparam name="TProvider"></typeparam>

    internal class ProviderLoader<TProvider>
        where TProvider : IProvider
    {
        private readonly Dictionary<string, TProvider> providers;
        private IDictionary<string, IProviderConfiguration> providerConfigs;
        private readonly Logger logger;

        public ProviderLoader()
        {
            logger = LogManager.GetLogger("ProviderLoader/" + typeof(TProvider).Name, LoggerType.Runtime);
            providers = new Dictionary<string, TProvider>();
        }

        public void LoadProviders(IDictionary<string, IProviderConfiguration> configs, IProviderManager providerManager)
        {
            providerConfigs = configs ?? new Dictionary<string, IProviderConfiguration>();

            foreach (IProviderConfiguration providerConfig in providerConfigs.Values)
            {
                ((ProviderConfiguration) providerConfig).SetProviderManager(providerManager);
            }

            // Load providers
            ProviderTypeLoader.AddProviderTypeManager(t => typeof(TProvider).IsAssignableFrom(t), RegisterProviderType);

            ValidateProviders();
        }

        // Close providers and then remove them from the list
        public async Task RemoveProviders(IList<string> providerNames)
        {
            List<Task> tasks = new List<Task>();
            int count = providers.Count;

            if (providerConfigs != null) count = providerConfigs.Count;
            foreach (string providerName in providerNames)
            {
                if (providerConfigs.ContainsKey(providerName)) providerConfigs.Remove(providerName);

                if (providers.ContainsKey(providerName))
                {
                    tasks.Add(providers[providerName].Close());
                    providers.Remove(providerName);
                }
            }
            
            await Task.WhenAll(tasks);
        }

        private void ValidateProviders()
        {
            foreach (IProviderConfiguration providerConfig in providerConfigs.Values)
            {
                TProvider provider;
                ProviderConfiguration fullConfig = (ProviderConfiguration) providerConfig;
                if (providers.TryGetValue(providerConfig.Name, out provider))
                {
                    logger.Verbose(ErrorCode.Provider_ProviderLoadedOk, 
                        "Provider of type {0} name {1} located ok.", 
                        fullConfig.Type, fullConfig.Name);
                    continue;
                }

                string msg = string.Format("Provider of type {0} name {1} was not loaded." +
                    "Please check that you deployed the assembly in which the provider class is defined to the execution folder.", 
                    fullConfig.Type, fullConfig.Name);
                logger.Error(ErrorCode.Provider_ConfiguredProviderNotLoaded, msg);
                throw new OrleansException(msg);
            }
        }


        public async Task InitProviders(IProviderRuntime providerRuntime)
        {
            Dictionary<string, TProvider> copy; 
            lock (providers)
            {
                copy = providers.ToDictionary(p => p.Key, p => p.Value);
            }

            foreach (var provider in copy)
            {
                string name = provider.Key;
                try
                {
                    await provider.Value.Init(provider.Key, providerRuntime, providerConfigs[name]);
                }
                catch (Exception exc)
                {
                    string exceptionMsg = string.Format("Exception initializing provider Name={0} Type={1}", name, provider);
                    logger.Error(ErrorCode.Provider_ErrorFromInit, exceptionMsg, exc);
#if !NETSTANDARD_TODO
                    //deserialize constructor of ProviderInitializationException not port to vNext yet
                    throw new ProviderInitializationException(exceptionMsg, exc);
#else
                    throw;
#endif
                }
            }
        }


        // used only for testing
        internal void AddProvider(string name, TProvider provider, IProviderConfiguration config)
        {
            lock (providers)
            {
                providers.Add(name, provider);
            }
        }

        internal int GetNumLoadedProviders()
        {
            lock (providers)
            {
                return providers.Count;
            }
        }

        public TProvider GetProvider(string name, bool caseInsensitive = false)
        {
            TProvider provider;
            if (!TryGetProvider(name, out provider, caseInsensitive))
            {
                throw new KeyNotFoundException(string.Format("Cannot find provider of type {0} with Name={1}", typeof(TProvider).FullName, name));
            }
            return provider;
        }

        public bool TryGetProvider(string name, out TProvider provider, bool caseInsensitive = false)
        {
            lock (providers)
            {
                if (providers.TryGetValue(name, out provider)) return provider != null;
                if (!caseInsensitive) return provider != null;

                // Try all lower case
                if (!providers.TryGetValue(name.ToLowerInvariant(), out provider))
                {
                    // Try all upper case
                    providers.TryGetValue(name.ToUpperInvariant(), out provider);
                }
            }
            return provider != null;
        }

        public IList<TProvider> GetProviders()
        {
            lock (providers)
            {
                return providers.Values.ToList();
            }
        }

        public TProvider GetDefaultProvider(string defaultProviderName)
        {
            lock (providers)
            {
                TProvider provider;
                // Use provider named "Default" if present
                if (!providers.TryGetValue(defaultProviderName, out provider))
                {
                    // Otherwise, if there is only a single provider listed, use that
                    if (providers.Count == 1) provider = providers.First().Value;
                }
                if (provider != null) return provider;

                string errMsg = "Cannot find default provider for " + typeof(TProvider);
                logger.Error(ErrorCode.Provider_NoDefaultProvider, errMsg);
                throw new InvalidOperationException(errMsg);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void RegisterProviderType(Type t)
        {
            // First, figure out the provider type name
            var typeName = TypeUtils.GetFullName(t);
            IList<String> providerNames;

            lock (providers)
            {
                providerNames = providers.Keys.ToList();
            }

            // Now see if we have any config entries for that type 
            // If there's no config entry, then we don't load the type
            Type[] constructorBindingTypes = new[] { typeof(string), typeof(XmlElement) };
            foreach (var entry in providerConfigs.Values)
            {
                var fullConfig = (ProviderConfiguration) entry;

                // Check if provider is already initialized. Skip loading it if so.
                if (providerNames.Contains(fullConfig.Name)) continue;

                if (fullConfig.Type != typeName) continue;
                
                // Found one! Now look for an appropriate constructor; try TProvider(string, Dictionary<string,string>) first
                var constructor = TypeUtils.GetConstructorThatMatches(t, constructorBindingTypes);
                var parms = new object[] { typeName, entry.Properties };

                if (constructor == null)
                {
                    // See if there's a default constructor to use, if there's no two-parameter constructor
                    constructor = TypeUtils.GetConstructorThatMatches(t, Type.EmptyTypes);
                    parms = new object[0];
                }
                if (constructor == null)
                {
                    logger.Warn(ErrorCode.Provider_InstanceConstructionError1, $"Accessible constructor does not exist for type {t.Name}");

                    continue;
                }

                TProvider instance;
                try
                {
                    instance = (TProvider)constructor.Invoke(parms);
                }
                catch (Exception ex)
                {
                    logger.Warn(ErrorCode.Provider_InstanceConstructionError1, "Error constructing an instance of a " + typeName +
                                                                               " provider using type " + t.Name + " for provider with name " + fullConfig.Name, ex);
                    return;
                }

                lock (providers)
                {
                    providers[fullConfig.Name] = instance;
                }
                
                logger.Info(ErrorCode.Provider_Loaded, "Loaded provider of type {0} Name={1}", typeName, fullConfig.Name);
            }
        }
    }
}
