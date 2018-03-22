using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.ReminderService
{
    internal class ReminderTableConfigurationValidator : IConfigurationValidator
    {
        internal const string ReminderServiceNotConfigured =
              "The reminder service has not been configured. Reminders can be configured using extension methods from the following packages:"
              + "\n  * Microsoft.Orleans.Reminders.AzureStorage via ISiloHostBuilder.UseAzureTableReminderService(...)"
              + "\n  * Microsoft.Orleans.Reminders.AdoNet via ISiloHostBuilder.UseAdoNetReminderService(...)"
              + "\n  * Microsoft.Orleans.Reminders.DynamoDB via via ISiloHostBuilder.UseDynamoDBReminderService(...)"
              + "\n  * Microsoft.Orleans.OrleansRuntime via ISiloHostBuilder.UseInMemoryReminderService(...) (Note: for development purposes only)"
              + "\n  * Others, see: https://www.nuget.org/packages?q=Microsoft.Orleans.Reminders.";
        private readonly GrainTypeManager grainTypeManager;
        private readonly IServiceProvider serviceProvider;

        public ReminderTableConfigurationValidator(GrainTypeManager grainTypeManager, IServiceProvider serviceProvider)
        {
            this.grainTypeManager = grainTypeManager;
            this.serviceProvider = serviceProvider;
        }

        public void ValidateConfiguration()
        {
            var reminderTable = this.serviceProvider.GetService<IReminderTable>();
            if (reminderTable != null) return;

            var allGrains = this.grainTypeManager.GrainClassTypeData.Select(data => data.Value);

            var remindableGrains = new List<Type>();
            foreach (var grain in allGrains)
            {
                if (typeof(IRemindable).IsAssignableFrom(grain.Type))
                {
                    remindableGrains.Add(grain.Type);
                }
            }

            if (remindableGrains.Count > 0)
            {
                var message = new StringBuilder(ReminderServiceNotConfigured);
                message.AppendLine("\nThe following grain classes require reminders:");

                foreach (var grain in remindableGrains)
                {
                    message.AppendLine($"  * {grain.GetParseableName(TypeFormattingOptions.LogFormat)}");
                }

                throw new OrleansConfigurationException(message.ToString());
            }
        }
    }
}