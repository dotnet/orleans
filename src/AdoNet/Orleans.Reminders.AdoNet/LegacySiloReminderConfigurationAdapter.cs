using Microsoft.Extensions.DependencyInjection;

using Orleans.Runtime.MembershipService;

namespace Orleans.Hosting
{
    /// <inheritdoc />
    internal class LegacySiloReminderConfigurationAdapter : ILegacyReminderTableAdapter
    {
        /// <inheritdoc />
        public void Configure(object configuration, IServiceCollection services)
        {
            var reader = new GlobalConfigurationReader(configuration);
            var connectionString = reader.GetPropertyValue<string>("DataConnectionStringForReminders");
            var invariant = reader.GetPropertyValue<string>("AdoInvariantForReminders");
            services.UseAdoNetReminderService(connectionString, invariant);
        }
    }
}