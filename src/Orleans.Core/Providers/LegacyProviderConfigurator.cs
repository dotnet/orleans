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

namespace Orleans.Providers
{
    public class LegacyProviderConfigurator
    {
        public const string InitStageName = "ProviderInitStage";
        public const string StartStageName = "ProviderStartStage";
        // Optional task scheduling behavior, may not always be set.
        internal delegate Task ScheduleTask(Func<Task> taskFunc);
    }

    internal static class LegacyProviderConfigurator<TLifecycle>
        where TLifecycle : ILifecycleObservable
    {
        /// <summary>
        /// Legacy way to configure providers. Will need to move to a legacy package in the future
        /// </summary>
        /// <returns></returns>
        internal static void ConfigureServices(IDictionary<string, ProviderCategoryConfiguration> providerConfigurations, IServiceCollection services, int defaultInitStage, int defaultStartStage)
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
                        RegisterProvider<IStreamProvider>(providerConfig, services, defaultInitStage, defaultStartStage);
                    }
                }
                else if (providerGroup.Key == ProviderCategoryConfiguration.STORAGE_PROVIDER_CATEGORY_NAME)
                {
                    services.AddSingleton<IStorageProvider>(sp => sp.GetServiceByName<IStorageProvider>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
                    foreach (IProviderConfiguration providerConfig in providerGroup.SelectMany(kvp => kvp.Value.Providers.Values))
                    {
                        RegisterProvider<IStorageProvider>(providerConfig, services, defaultInitStage, defaultStartStage);
                    }
                }
                else if (providerGroup.Key == ProviderCategoryConfiguration.LOG_CONSISTENCY_PROVIDER_CATEGORY_NAME)
                {
                    services.AddSingleton<ILogConsistencyProvider>(sp => sp.GetServiceByName<ILogConsistencyProvider>(ProviderConstants.DEFAULT_LOG_CONSISTENCY_PROVIDER_NAME));
                    foreach (IProviderConfiguration providerConfig in providerGroup.SelectMany(kvp => kvp.Value.Providers.Values))
                    {
                        RegisterProvider<ILogConsistencyProvider>(providerConfig, services, defaultInitStage, defaultStartStage);
                    }
                }
                else if (providerGroup.Key == ProviderCategoryConfiguration.STATISTICS_PROVIDER_CATEGORY_NAME)
                {
                    IProviderConfiguration providerConfig = providerGroup.SelectMany(kvp => kvp.Value.Providers.Values).FirstOrDefault();
                    if(providerConfig != null)
                    {
                        // Looks like we only support a single statistics provider that can be any of the publisher interfaces. fml
                        // TODO: Kill our statistics system.. please!?
                        services.AddSingleton<IStatisticsPublisher>(sp => sp.GetServiceByName<IProvider>(providerConfig.Name) as IStatisticsPublisher);
                        services.AddSingleton<ISiloMetricsDataPublisher>(sp => sp.GetServiceByName<IProvider>(providerConfig.Name) as ISiloMetricsDataPublisher);
                        services.AddSingleton<IClientMetricsDataPublisher>(sp => sp.GetServiceByName<IProvider>(providerConfig.Name) as IClientMetricsDataPublisher);
                        RegisterProvider<IProvider>(providerConfig, services, defaultInitStage, defaultStartStage);
                    }
                }
                else
                {
                    foreach (IProviderConfiguration providerConfig in providerGroup.SelectMany(kvp => kvp.Value.Providers.Values))
                    {
                        RegisterProvider<IProvider>(providerConfig, services, defaultInitStage, defaultStartStage);
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
            private readonly LegacyProviderConfigurator.ScheduleTask schedule;
            private readonly int defaultInitStage;
            private int initStage;
            private readonly int defaultStartStage;
            private int startStage;

            public ProviderLifecycleParticipant(IProviderConfiguration config, IServiceProvider services, ILoggerFactory loggerFactory, int defaultInitStage, int defaultStartStage)
            {
                this.logger = loggerFactory.CreateLogger(config.Type);
                this.services = services;
                this.config = config;
                this.defaultInitStage = defaultInitStage;
                this.defaultStartStage = defaultStartStage;
                this.schedule = services.GetService<LegacyProviderConfigurator.ScheduleTask>();
            }

            public virtual void Participate(TLifecycle lifecycle)
            {
                this.initStage = this.config.GetIntProperty(LegacyProviderConfigurator.InitStageName, this.defaultInitStage);
                lifecycle.Subscribe(this.initStage, Init, Close);
                this.startStage = this.config.GetIntProperty(LegacyProviderConfigurator.StartStageName, this.defaultStartStage);
                lifecycle.Subscribe(this.startStage, Start);

            }

            private async Task Init(CancellationToken ct)
            {
                var stopWatch = Stopwatch.StartNew();
                IProvider provider = this.services.GetServiceByName<TService>(this.config.Name) as IProvider;
                IProviderRuntime runtime = this.services.GetRequiredService<IProviderRuntime>();
                await Schedule(() => provider.Init(this.config.Name, runtime, this.config));
                stopWatch.Stop();
                this.logger.Info(ErrorCode.SiloStartPerfMeasure, $"Initializing provider {this.config.Name} of type {this.config.Type} in stage {this.initStage} took {stopWatch.ElapsedMilliseconds} Milliseconds.");
            }

            private async Task Close(CancellationToken ct)
            {
                var stopWatch = Stopwatch.StartNew();
                IProvider provider = this.services.GetServiceByName<TService>(this.config.Name) as IProvider;
                await Schedule(() => provider.Close());
                stopWatch.Stop();
                this.logger.Info(ErrorCode.SiloStartPerfMeasure, $"Closing provider {this.config.Name} of type {this.config.Type} in stage {this.initStage} took {stopWatch.ElapsedMilliseconds} Milliseconds.");
            }

            private async Task Start(CancellationToken ct)
            {
                var stopWatch = Stopwatch.StartNew();
                IStreamProviderImpl provider = this.services.GetServiceByName<TService>(this.config.Name) as IStreamProviderImpl;
                if (provider == null) return;
                await Schedule(() => provider.Start());
                stopWatch.Stop();
                this.logger.Info(ErrorCode.SiloStartPerfMeasure, $"Starting provider {this.config.Name} of type {this.config.Type} in stage {this.startStage} took {stopWatch.ElapsedMilliseconds} Milliseconds.");
            }

            private Task Schedule(Func<Task> taskFunc)
            {
                return this.schedule != null
                    ? this.schedule.Invoke(taskFunc)
                    : taskFunc();
            }
        }

        private static void RegisterProvider<TService>(IProviderConfiguration config, IServiceCollection services, int defaultInitStage, int defaultStartStage)
            where TService : class
        {
            services.AddSingletonNamedService<TService>(config.Name, (s, n) => {
                Type providerType = s.GetRequiredService<ProviderTypeLookup>().GetType(config.Type);
                return Activator.CreateInstance(providerType) as TService;
            });
            services.AddSingletonNamedService<ILifecycleParticipant<TLifecycle>>(config.Name, (s, n) => new ProviderLifecycleParticipant<TService>(config, s, s.GetRequiredService<ILoggerFactory>(), defaultInitStage, defaultStartStage));
            services.AddSingletonNamedService<IControllable>(config.Name, (s, n) => s.GetServiceByName<TService>(n) as IControllable);
        }
    }
}
