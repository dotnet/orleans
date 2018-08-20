using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.Tests.DeactivatingInjection;

namespace Orleans.Transactions.Tests.DeactivationTransaction
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure cluster to use the distributed TM algorithm
        /// </summary>
        public static ISiloHostBuilder UseDeactivationTransactionState(this ISiloHostBuilder builder)
        {
            return builder.ConfigureServices(services => services.UseDeactivationTransactionState());
        }

        /// <summary>
        /// Configure cluster to use the distributed TM algorithm
        /// </summary>
        public static IServiceCollection UseDeactivationTransactionState(this IServiceCollection services)
        {
            services.AddSingleton<IAttributeToFactoryMapper<DeactivationTransactionalStateAttribute>, DeactivationTransactionalStateAttributeMapper>();
            services.TryAddTransient<IDeactivationTransactionalStateFactory, DeactivationalTransactionalStateFactory>();
            services.AddTransient(typeof(IDeactivationTransactionalState<>), typeof(DeactivationTransactionalState<>));
            return services;
        }
    }
}
