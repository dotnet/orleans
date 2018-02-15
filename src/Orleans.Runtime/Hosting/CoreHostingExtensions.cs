using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions for <see cref="ISiloHostBuilder"/> instances.
    /// </summary>
    public static class CoreHostingExtensions
    {
        /// <summary>
        /// Configure the container to use Orleans, including the default silo name & services.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="configureOptions">The delegate that configures the options.</param>
        /// <returns>The host builder.</returns>
        public static ISiloHostBuilder ConfigureOrleans(this ISiloHostBuilder builder, Action<SiloOptions> configureOptions)
        {
            var optionsConfigurator = configureOptions != null
                ? ob => ob.Configure(configureOptions)
                : default(Action<OptionsBuilder<SiloOptions>>);

            return builder.ConfigureOrleans(optionsConfigurator);
        }

        /// <summary>
        /// Configure the container to use Orleans, including the default silo name & services.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="configureOptions">The delegate that configures the options using the options builder.</param>
        /// <returns>The host builder.</returns>
        public static ISiloHostBuilder ConfigureOrleans(this ISiloHostBuilder builder, Action<OptionsBuilder<SiloOptions>> configureOptions = null)
        {
            builder.ConfigureServices((context, services) =>
            {
                if (!context.Properties.ContainsKey("OrleansServicesAdded"))
                {
                    services.PostConfigure<SiloOptions>(options => options.SiloName = options.SiloName
                                           ?? context.HostingEnvironment.ApplicationName
                                           ?? $"Silo_{Guid.NewGuid().ToString("N").Substring(0, 5)}");

                    services.TryAddSingleton<Silo>();
                    DefaultSiloServices.AddDefaultServices(context, services);

                    context.Properties.Add("OrleansServicesAdded", true);
                }

                configureOptions?.Invoke(services.AddOptions<SiloOptions>());
            });
            return builder;
        }

        /// <summary>
        /// Configures the name of this silo.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="siloName">The silo name.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloHostBuilder ConfigureSiloName(this ISiloHostBuilder builder, string siloName)
        {
            builder.Configure<SiloOptions>(options => options.SiloName = siloName);
            return builder;
        }

        /// <summary>
        /// Configure silo to use Development membership
        /// </summary>
        public static ISiloHostBuilder UseDevelopmentClustering(this ISiloHostBuilder builder, Action<DevelopmentMembershipOptions> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    if (configureOptions != null)
                    {
                        services.Configure(configureOptions);
                    }

                    services
                        .AddSingleton<GrainBasedMembershipTable>()
                        .AddFromExisting<IMembershipTable, GrainBasedMembershipTable>();
                });
        }

        /// <summary>
        /// Configure silo to use Development membership
        /// </summary>
        public static ISiloHostBuilder UseDevelopmentClustering(this ISiloHostBuilder builder, Action<OptionsBuilder<DevelopmentMembershipOptions>> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    configureOptions?.Invoke(services.AddOptions<DevelopmentMembershipOptions>());
                    services
                        .AddSingleton<GrainBasedMembershipTable>()
                        .AddFromExisting<IMembershipTable, GrainBasedMembershipTable>();
                });
        }
    }
}