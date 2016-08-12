using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.ReminderService;

namespace Orleans.Runtime.Startup
{
    /// <summary>
    /// Configure dependency injection at startup
    /// </summary>
    internal class StartupBuilder
    {
        internal static void RegisterSystemTypes(IServiceCollection serviceCollection)
        {
            // Register the system classes and grains in this method.
            // Note: this method will probably have to be moved out into the Silo class to include internal runtime types.

            serviceCollection.AddTransient<GrainBasedMembershipTable>();
            serviceCollection.AddTransient<GrainBasedReminderTable>();
        }
    }
}
