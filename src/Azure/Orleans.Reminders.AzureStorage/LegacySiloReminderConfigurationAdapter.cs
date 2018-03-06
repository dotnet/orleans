using Microsoft.Extensions.DependencyInjection;

using Orleans.Runtime.MembershipService;

namespace Orleans.Hosting
{
    /// <inheritdoc />
    internal class LegacySiloReminderConfigurationAdapter : ILegacyReminderTableAdapter
    {
        /// <inheritdoc />
        public void Configure(object configuration, ISiloHostBuilder builder)
        {
            var reader = new GlobalConfigurationReader(configuration);
            var connectionString = reader.GetPropertyValue<string>("DataConnectionStringForReminders");
            builder.UseAzureTableReminderService(connectionString);
        }
    }
}