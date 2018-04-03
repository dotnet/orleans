using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
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
            var invariant = reader.GetPropertyValue<string>("AdoInvariantForReminders");
            builder.UseAdoNetReminderService(options =>
            {
                options.ConnectionString = connectionString;
                options.Invariant = invariant;
            });
        }
    }
}