using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.LogConsistency;
using Orleans.Runtime.Configuration;
using Orleans.Storage;
using Orleans.Streams;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Orleans.Providers
{
    public class LegacyProviderConfigurator
    {
        public const string InitStageName = "ProviderInitStage";
    }

    internal static class LegacyProviderConfigurator<TLifecycle>
        where TLifecycle : ILifecycleObservable
    {
        public const int DefaultStage = ServiceLifecycleStage.RuntimeStorageServices;

        /// <summary>
        /// Legacy way to configure providers. Will need to move to a legacy package in the future
        /// </summary>
        /// <returns></returns>
        internal static void ConfigureServices(IDictionary<string, ProviderCategoryConfiguration> providerConfigurations, IServiceCollection services)
        {
            // if already added providers or nothing to add, skipp
            if (services.Any(s => s.ServiceType == typeof(ProviderTypeLookup))
                || providerConfigurations.Count == 0)
                return;

            services.AddSingleton<ProviderTypeLookup>();

            foreach (var providerGroup in providerConfigurations.GroupBy(p => p.Key))
            {
                if (providerGroup.Key == ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME)
                {
                    foreach (IProviderConfiguration providerConfig in providerGroup.SelectMany(kvp => kvp.Value.Providers.Values))
                    {
                        RegisterProvider<IStreamProvider>(providerConfig, services, DefaultStage);
                    }
                }
                else if (providerGroup.Key == ProviderCategoryConfiguration.STORAGE_PROVIDER_CATEGORY_NAME)
                {
                    services.TryAddSingleton<IGrainStorage>(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
                    foreach (IProviderConfiguration providerConfig in providerGroup.SelectMany(kvp => kvp.Value.Providers.Values))
                    {
                        RegisterProvider<IGrainStorage>(providerConfig, services, DefaultStage);
                    }
                }
                else if (providerGroup.Key == ProviderCategoryConfiguration.LOG_CONSISTENCY_PROVIDER_CATEGORY_NAME)
                {
                    services.AddSingleton<ILogViewAdaptorFactory>(sp => sp.GetServiceByName<ILogViewAdaptorFactory>(ProviderConstants.DEFAULT_LOG_CONSISTENCY_PROVIDER_NAME));
                    foreach (IProviderConfiguration providerConfig in providerGroup.SelectMany(kvp => kvp.Value.Providers.Values))
                    {
                        RegisterProvider<ILogViewAdaptorFactory>(providerConfig, services, DefaultStage);
                    }
                }
                else
                {
                    foreach (IProviderConfiguration providerConfig in providerGroup.SelectMany(kvp => kvp.Value.Providers.Values))
                    {
                        RegisterProvider<IProvider>(providerConfig, services, DefaultStage);
                    }
                }
            }
        }

        private class ProviderLifecycleParticipant<TService> : ILifecycleParticipant<TLifecycle>
            where TService : class
        {
            private readonly ILogger logger;
            private readonly IProviderConfiguration config;
            private readonly IServiceProvider services;
            private readonly int defaultInitStage;
            private int initStage;
            private Lazy<IProvider> provider;

            public ProviderLifecycleParticipant(IProviderConfiguration config, IServiceProvider services, ILoggerFactory loggerFactory, int defaultInitStage)
            {
                this.logger = loggerFactory.CreateLogger(config.Type);
                this.services = services;
                this.config = config;
                this.defaultInitStage = defaultInitStage;
                this.provider = new Lazy<IProvider>(() => services.GetServiceByName<TService>(this.config.Name) as IProvider);
            }

            public virtual void Participate(TLifecycle lifecycle)
            {
                this.initStage = this.config.GetIntProperty(LegacyProviderConfigurator.InitStageName, this.defaultInitStage);
                lifecycle.Subscribe($"LegacyProvider-{typeof(TService).FullName}-{config.Type}-{config.Name}", this.initStage, this.Init, this.ProviderClose);

            }

            private async Task Init(CancellationToken ct)
            {
                var stopWatch = Stopwatch.StartNew();
                try
                {
                    IProvider provider = this.provider.Value;
                    IProviderRuntime runtime = this.services.GetRequiredService<IProviderRuntime>();
                    await provider.Init(this.config.Name, runtime, this.config);
                    stopWatch.Stop();
                    this.logger.Info(ErrorCode.SiloStartPerfMeasure, $"Initializing provider {this.config.Name} of type {this.config.Type} in stage {this.initStage} took {stopWatch.ElapsedMilliseconds} Milliseconds.");
                } catch(Exception ex)
                {
                    stopWatch.Stop();
                    this.logger.Error(ErrorCode.Provider_ErrorFromInit, $"Initialization failed for provider {this.config.Name} of type {this.config.Type} in stage {this.initStage} in {stopWatch.ElapsedMilliseconds} Milliseconds.", ex);
                    throw;
                }
            }

            private async Task ProviderClose(CancellationToken ct)
            {
                var stopWatch = Stopwatch.StartNew();
                try
                {
                    IProvider provider = this.provider.Value;
                    await provider.Close();
                    stopWatch.Stop();
                    this.logger.Info(ErrorCode.SiloStartPerfMeasure, $"Closing provider {this.config.Name} of type {this.config.Type} in stage {this.initStage} took {stopWatch.ElapsedMilliseconds} Milliseconds.");
                }
                catch (Exception ex)
                {
                    stopWatch.Stop();
                    this.logger.Error(ErrorCode.Provider_ErrorFromClose, $"Close failed for provider {this.config.Name} of type {this.config.Type} in stage {this.initStage} in {stopWatch.ElapsedMilliseconds} Milliseconds.", ex);
                    throw;
                }
            }
        }

        private static void RegisterProvider<TService>(IProviderConfiguration config, IServiceCollection services, int defaultInitStage)
            where TService : class
        {
            services.AddSingletonNamedService<TService>(config.Name, (s, n) => {
                Type providerType = s.GetRequiredService<ProviderTypeLookup>().GetType(config.Type);
                return Activator.CreateInstance(providerType) as TService;
            });
            services.AddSingletonNamedService<ILifecycleParticipant<TLifecycle>>(config.Name, (s, n) => new ProviderLifecycleParticipant<TService>(config, s, s.GetRequiredService<ILoggerFactory>(), defaultInitStage));
            services.AddSingletonNamedService(config.Name, (s, n) => s.GetServiceByName<TService>(n) as IControllable);
        }
    }
}
