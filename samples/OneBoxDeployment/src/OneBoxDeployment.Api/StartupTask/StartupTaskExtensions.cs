using Microsoft.Extensions.DependencyInjection;
using System.Linq;

namespace OneBoxDeployment.Api.StartupTask
{
    /// <summary>
    /// Some asynchronous startup task extensions.
    /// </summary>
    /// <remarks>Based on code by Andrew Lock at https://andrewlock.net/running-async-tasks-on-app-startup-in-asp-net-core-part-4-using-health-checks/.
    /// See some problems and improvements
    /// <ul>
    ///     <li>https://tools.ietf.org/html/draft-inadarei-api-health-check-02</li>
    ///     <li>https://github.com/aspnet/AspNetCore/issues/5936</li>
    /// </ul>
    /// </remarks>
    public static class StartupTaskExtensions
    {
        private static readonly StartupTaskContext _sharedContext = new StartupTaskContext();

        public static IServiceCollection AddStartupTasks(this IServiceCollection services)
        {
            // Don't add StartupTaskContext if we've already added it
            if(services.Any(x => x.ServiceType == typeof(StartupTaskContext)))
            {
                return services;
            }

            return services.AddSingleton(_sharedContext);
        }


        public static IServiceCollection AddStartupTask<T>(this IServiceCollection services) where T: class, IStartupTask
        {
            _sharedContext.RegisterTask();
            return services
                .AddStartupTasks() // in case AddStartupTasks() hasn't been called
                .AddHostedService<T>();
        }
    }
}
