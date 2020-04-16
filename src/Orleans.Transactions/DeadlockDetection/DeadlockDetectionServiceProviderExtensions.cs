using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans.Transactions.DeadlockDetection
{
    public static class DeadlockDetectionServiceProviderExtensions
    {
        public static IServiceCollection UseTransactionalDeadlockDetection(this IServiceCollection serviceCollection) =>
            serviceCollection.AddSingleton<ITransactionalLockObserver, DeadlockDetectionLockObserver>()
                .AddSingleton<ILocalDeadlockDetector>(sp =>
                    (DeadlockDetectionLockObserver)sp.GetRequiredService<ITransactionalLockObserver>());
    }
}