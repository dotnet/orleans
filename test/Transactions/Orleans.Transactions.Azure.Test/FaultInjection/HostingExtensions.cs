using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AzureStorage;

namespace Orleans.Transactions.Azure.Tests.FaultInjection
{
    public static class HostingExtensions
    {

        public static ISiloHostBuilder AddFaultInjectionAzureTableTransactionalStateStorage(this ISiloHostBuilder builder, Action<AzureTableTransactionalStateOptions> configureOptions)
        {
            return builder.AddFaultInjectionAzureTableTransactionalStateStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        public static ISiloHostBuilder AddFaultInjectionAzureTableTransactionalStateStorage(this ISiloHostBuilder builder, string name, Action<AzureTableTransactionalStateOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddFaultInjectionAzureTableTransactionalStateStorage(name, ob => ob.Configure(configureOptions)));
        }

        private static IServiceCollection AddFaultInjectionAzureTableTransactionalStateStorage(this IServiceCollection services, string name,
            Action<OptionsBuilder<AzureTableTransactionalStateOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<AzureTableTransactionalStateOptions>(name));

            services.TryAddSingleton<ITransactionalStateStorageFactory>(sp => sp.GetServiceByName<ITransactionalStateStorageFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            services.AddSingletonNamedService<ITransactionalStateStorageFactory>(name, FaultInjectionAzureTableTransactionStateStorageFactory.Create);
            services.AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<ITransactionalStateStorageFactory>(n));

            return services;
        }
    }
}
