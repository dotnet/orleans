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
        /// Specifies the configuration to use for this silo.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="configuration">The configuration.</param>
        /// <remarks>This method may only be called once per builder instance.</remarks>
        /// <returns>The silo builder.</returns>
        public static ISiloHostBuilder UseConfiguration(this ISiloHostBuilder builder, ClusterConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            return builder.ConfigureServices((context, services) =>
            {
                services.AddLegacyClusterConfigurationSupport(configuration);
            });
        }

        /// <summary>
        /// Loads <see cref="ClusterConfiguration"/> using <see cref="ClusterConfiguration.StandardLoad"/>.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloHostBuilder LoadClusterConfiguration(this ISiloHostBuilder builder)
        {
            var configuration = new ClusterConfiguration();
            configuration.StandardLoad();
            return builder.UseConfiguration(configuration);
        }
        
        /// <summary>
        /// Configures a localhost silo.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="siloPort">The silo-to-silo communication port.</param>
        /// <param name="gatewayPort">The client-to-silo communication port.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloHostBuilder ConfigureLocalHostPrimarySilo(this ISiloHostBuilder builder, int siloPort = 22222, int gatewayPort = 40000)
        {
            builder.ConfigureSiloName(Silo.PrimarySiloName);
            return builder.UseConfiguration(ClusterConfiguration.LocalhostPrimarySilo(siloPort, gatewayPort));
        }

        /// <summary>
        /// Configure silo to use Development membership
        /// </summary>
        public static ISiloHostBuilder UseDevelopmentMembership(this ISiloHostBuilder builder, Action<DevelopmentMembershipOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseDevelopmentMembership(configureOptions));
        }

        /// <summary>
        /// Configure silo to use Development membership
        /// </summary>
        public static ISiloHostBuilder UseDevelopmentMembership(this ISiloHostBuilder builder, Action<OptionsBuilder<DevelopmentMembershipOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseDevelopmentMembership(configureOptions));
        }

        /// <summary>
        /// Configure silo to use Development membership
        /// </summary>
        public static IServiceCollection UseDevelopmentMembership(this IServiceCollection services, Action<DevelopmentMembershipOptions> configureOptions)
        {
            return services.UseDevelopmentMembership(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use Development membership
        /// </summary>
        public static IServiceCollection UseDevelopmentMembership(this IServiceCollection services, Action<OptionsBuilder<DevelopmentMembershipOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<DevelopmentMembershipOptions>());
            services
                .AddSingleton<GrainBasedMembershipTable>()
                .AddFromExisting<IMembershipTable, GrainBasedMembershipTable>();

            return services;
        }
    }
}