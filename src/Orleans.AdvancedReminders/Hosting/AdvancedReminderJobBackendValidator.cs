using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.AdvancedReminders.Runtime.ReminderService;

namespace Orleans.AdvancedReminders.Runtime.Hosting;

internal sealed class AdvancedReminderJobBackendValidator(IServiceProvider serviceProvider) : IConfigurationValidator
{
    public void ValidateConfiguration()
    {
        if (!IsServiceRegistered(typeof(JobShardManager)))
        {
            throw new OrleansConfigurationException(
                "AdvancedReminders requires a durable jobs backend. Configure UseInMemoryDurableJobs() or a storage-backed durable jobs provider before starting the silo.");
        }

        if (!IsServiceRegistered(typeof(IReminderTable)))
        {
            throw new OrleansConfigurationException(
                "AdvancedReminders requires a reminder table provider. Configure UseInMemoryAdvancedReminderService() or a storage-backed advanced reminder provider before starting the silo.");
        }

        var durableJobsOptions = serviceProvider.GetRequiredService<IOptions<DurableJobsOptions>>().Value;
        if (durableJobsOptions.ShardLoadLookaheadPeriod < AdvancedReminderRecoveryGrain.MinimumLookaheadPeriod)
        {
            throw new OrleansConfigurationException(
                $"{nameof(DurableJobsOptions)}.{nameof(DurableJobsOptions.ShardLoadLookaheadPeriod)} must be at least {AdvancedReminderRecoveryGrain.MinimumLookaheadPeriod} when AdvancedReminders is enabled.");
        }
    }

    private bool IsServiceRegistered(Type serviceType)
        => serviceProvider.GetService<IServiceProviderIsService>() is { } serviceProviderIsService
            ? serviceProviderIsService.IsService(serviceType)
            : serviceProvider.GetService(serviceType) is not null;
}
