using System;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.ReminderService
{
    internal static class ReminderTable
    {
        internal static IReminderTable Singleton { get; private set; }

        public static void Initialize(Silo silo, IGrainFactory grainFactory, string reminderTableAssembly = null)
        {
            var config = silo.GlobalConfig;
            var serviceType = config.ReminderServiceType;
            var logger = LogManager.GetLogger("ReminderTable");

            switch (serviceType)
            {
                default:
                    throw new NotSupportedException(
                        String.Format(
                            "The reminder table does not currently support service provider {0}.",
                            serviceType));

                case GlobalConfiguration.ReminderServiceProviderType.SqlServer:
                    Singleton = AssemblyLoader.LoadAndCreateInstance<IReminderTable>(Constants.ORLEANS_SQL_UTILS_DLL, logger);
                    return;

                case GlobalConfiguration.ReminderServiceProviderType.AzureTable:
                    Singleton = AssemblyLoader.LoadAndCreateInstance<IReminderTable>(Constants.ORLEANS_AZURE_UTILS_DLL, logger);
                    return;

                case GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain:
                    Singleton = grainFactory.GetGrain<IReminderTableGrain>(Constants.ReminderTableGrainId);
                    return;

                case GlobalConfiguration.ReminderServiceProviderType.MockTable:
                    Singleton = new MockReminderTable(config.MockReminderTableTimeout);
                    return;

                case GlobalConfiguration.ReminderServiceProviderType.Custom:
                    Singleton = AssemblyLoader.LoadAndCreateInstance<IReminderTable>(reminderTableAssembly, logger);
                    return;
            }
        }

        public static Task TestOnlyClearTable()
        {
            return Singleton.TestOnlyClearTable();
        }
    }
}
