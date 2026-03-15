using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.DurableJobs;

namespace Orleans.AdvancedReminders.Runtime.Hosting;

internal sealed class AdvancedReminderJobBackendValidator(IServiceProvider serviceProvider) : IConfigurationValidator
{
    public void ValidateConfiguration()
    {
        if (serviceProvider.GetService<JobShardManager>() is null)
        {
            throw new OrleansConfigurationException(
                "AdvancedReminders requires a durable jobs backend. Configure UseInMemoryDurableJobs() or a storage-backed durable jobs provider before starting the silo.");
        }
    }
}
