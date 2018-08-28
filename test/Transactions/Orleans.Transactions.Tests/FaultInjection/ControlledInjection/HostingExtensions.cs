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
        public static ISiloHostBuilder UseControlledFaultInjectionTransactionState(this ISiloHostBuilder builder)
        {
            return builder.ConfigureServices(services => services.UseControlledFaultInjectionTransactionState());
        }

        /// <summary>
        /// Configure cluster to use the distributed TM algorithm
        /// </summary>
        public static IServiceCollection UseControlledFaultInjectionTransactionState(this IServiceCollection services)
        {
            services.AddSingleton<IAttributeToFactoryMapper<FaultInjectionTransactionalStateAttribute>, FaultInjectionTransactionalStateAttributeMapper>();
            services.TryAddTransient<IFaultInjectionTransactionalStateFactory, FaultInjectionTransactionalStateFactory>();
            services.AddTransient(typeof(IFaultInjectionTransactionalState<>), typeof(FaultInjectionTransactionalState<>));
            return services;
        }
    }
}
