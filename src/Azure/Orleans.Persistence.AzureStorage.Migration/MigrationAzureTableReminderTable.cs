using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Persistence.AzureStorage.Migration.Reminders.Storage;
using Orleans.Persistence.Migration;
using Orleans.Reminders.AzureStorage;
using Orleans.Reminders.AzureStorage.Storage.Reminders;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;

namespace Orleans.Persistence.AzureStorage.Migration
{
    internal class MigrationAzureTableReminderTable : AzureBasedReminderTable
    {
        public MigrationAzureTableReminderTable(
            IGrainReferenceConverter grainReferenceConverter,
            ILoggerFactory loggerFactory,
            ClusterOptions clusterOptions,
            AzureTableReminderStorageOptions storageOptions,
            IReminderTableEntryBuilder builder)
            : base(grainReferenceConverter, loggerFactory, clusterOptions, storageOptions, builder)
        {
        }

        internal static new IReminderTable Create(IServiceProvider serviceProvider, string name)
        {
            var grainRefConverter = serviceProvider.GetService<IGrainReferenceConverter>();
            var grainRefExtractor = serviceProvider.GetService<IGrainReferenceExtractor>();

            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var clusterOptions = serviceProvider.GetRequiredService<IOptions<ClusterOptions>>();
            var storageOptions = serviceProvider.GetRequiredService<IOptionsMonitor<AzureTableReminderStorageOptions>>().Get(name);
            var reminderTableEntryBuilder = new MigratedReminderTableEntryBuilder(grainRefExtractor);

            return new MigrationAzureTableReminderTable(grainRefConverter, loggerFactory, clusterOptions.Value, storageOptions, reminderTableEntryBuilder);
        }
    }
}
