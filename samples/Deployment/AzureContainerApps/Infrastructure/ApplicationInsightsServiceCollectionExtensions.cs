using Infrastructure;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.Extensibility;

namespace Infrastructure
{
    public class ApplicationMapNodeNameInitializer : ITelemetryInitializer
    {
        public ApplicationMapNodeNameInitializer(string name)
        {
            Name = name;
        }

        public string Name { get; set; }

        public void Initialize(ITelemetry telemetry)
        {
            telemetry.Context.Cloud.RoleName = Name;
        }
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    public static class ApplicationInsightsServiceCollectionExtensions
    {
        public static void AddWebAppApplicationInsights(this IServiceCollection services, string applicationName)
        {
            services.AddApplicationInsightsTelemetry();
            services.AddSingleton<ITelemetryInitializer>((services) => new ApplicationMapNodeNameInitializer(applicationName));
        }

        public static void AddWorkerAppApplicationInsights(this IServiceCollection services, string applicationName)
        {
            services.AddApplicationInsightsTelemetryWorkerService();
            services.AddSingleton<ITelemetryInitializer>((services) => new ApplicationMapNodeNameInitializer(applicationName));
        }
    }
}