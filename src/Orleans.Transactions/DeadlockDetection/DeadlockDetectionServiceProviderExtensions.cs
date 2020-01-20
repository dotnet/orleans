using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans.Transactions.DeadlockDetection
{
    public static class DeadlockDetectionServiceProviderExtensions
    {
        public static IServiceCollection AddSimpleDeadlockDetection(this IServiceCollection serviceCollection) =>
            serviceCollection
                .AddSingletonNamedService(SimpleTransactionalLockObserver.ProviderName,
                    SimpleTransactionalLockObserver.Create).AddSingleton<ITransactionalLockObserver>(sp =>
                    (ITransactionalLockObserver)sp.GetRequiredServiceByName<IControllable>(
                        SimpleTransactionalLockObserver.ProviderName));
    }
}