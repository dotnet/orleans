using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Configuration.Options;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using Orleans.ApplicationParts;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Extension methods for <see cref="IClientBuilder"/>.
    /// </summary>
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configures default client services.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="siloName">The silo name.</param>
        /// <returns>The silo builder.</returns>
        public static IClientBuilder ConfigureDefaults(this IClientBuilder builder)
        {
            // Configure the container to use an Orleans client.
            builder.ConfigureServices(services =>
            {
                const string key = "OrleansClientServicesAdded";
                if (!builder.Properties.ContainsKey(key))
                {
                    DefaultClientServices.AddDefaultServices(builder, services);
                    builder.Properties.Add(key, true);
                }
            });
            return builder;
        }

        /// <summary>
        /// Loads configuration from the standard client configuration locations.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <remarks>
        /// This method loads the first client configuration file it finds, searching predefined directories for predefined file names.
        /// The following file names are tried in order:
        /// <list type="number">
        ///     <item>ClientConfiguration.xml</item>
        ///     <item>OrleansClientConfiguration.xml</item>
        ///     <item>Client.config</item>
        ///     <item>Client.xml</item>
        /// </list>
        /// The following directories are searched in order:
        /// <list type="number">
        ///     <item>The directory of the executing assembly.</item>
        ///     <item>The approot directory.</item>
        ///     <item>The current working directory.</item>
        ///     <item>The parent of the current working directory.</item>
        /// </list>
        /// Each directory is searched for all configuration file names before proceeding to the next directory.
        /// </remarks>
        /// <returns>The builder.</returns>
        public static IClientBuilder LoadConfiguration(this IClientBuilder builder)
        {
            builder.UseConfiguration(ClientConfiguration.StandardLoad());
            return builder;
        }

        /// <summary>
        /// Loads configuration from the provided location.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configurationFilePath"></param>
        /// <returns>The builder.</returns>
        public static IClientBuilder LoadConfiguration(this IClientBuilder builder, string configurationFilePath)
        {
            builder.LoadConfiguration(new FileInfo(configurationFilePath));
            return builder;
        }

        /// <summary>
        /// Loads configuration from the provided location.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configurationFile"></param>
        /// <returns>The builder.</returns>
        public static IClientBuilder LoadConfiguration(this IClientBuilder builder, FileInfo configurationFile)
        {
            var config = ClientConfiguration.LoadFromFile(configurationFile.FullName);
            if (config == null)
            {
                throw new ArgumentException(
                    $"Error loading client configuration file {configurationFile.FullName}",
                    nameof(configurationFile));
            }

            builder.UseConfiguration(config);
            return builder;
        }

        /// <summary>
        /// Adds a client invocation callback.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="callback">The callback.</param>
        /// <remarks>
        /// A <see cref="ClientInvokeCallback"/> ia a global pre-call interceptor.
        /// Synchronous callback made just before a message is about to be constructed and sent by a client to a grain.
        /// This call will be made from the same thread that constructs the message to be sent, so any thread-local settings
        /// such as <c>Orleans.RequestContext</c> will be picked up.
        /// The action receives an <see cref="InvokeMethodRequest"/> with details of the method to be invoked, including InterfaceId and MethodId,
        /// and a <see cref="IGrain"/> which is the GrainReference this request is being sent through
        /// This callback method should return promptly and do a minimum of work, to avoid blocking calling thread or impacting throughput.
        /// </remarks>
        /// <returns>The builder.</returns>
        public static IClientBuilder AddClientInvokeCallback(this IClientBuilder builder, ClientInvokeCallback callback)
        {
            builder.ConfigureServices(services => services.AddSingleton(callback));
            return builder;
        }

        /// <summary>
        /// Registers a <see cref="ConnectionToClusterLostHandler"/> event handler.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="handler">The handler.</param>
        /// <returns>The builder.</returns>
        public static IClientBuilder AddClusterConnectionLostHandler(this IClientBuilder builder, ConnectionToClusterLostHandler handler)
        {
            builder.ConfigureServices(services => services.AddSingleton(handler));
            return builder;
        }

        /// <summary>
        /// Specifies how the <see cref="IServiceProvider"/> for this client is configured. 
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="factory">The service provider factory.</param>
        /// <returns>The builder.</returns>
        public static IClientBuilder UseServiceProviderFactory(this IClientBuilder builder, Func<IServiceCollection, IServiceProvider> factory)
        {
            builder.UseServiceProviderFactory(new DelegateServiceProviderFactory(factory));
            return builder;
        }

        /// <summary>
        /// Adds a delegate for configuring the provided <see cref="ILoggingBuilder"/>. This may be called multiple times.
        /// </summary>
        /// <param name="builder">The <see cref="IClientBuilder" /> to configure.</param>
        /// <param name="configureLogging">The delegate that configures the <see cref="ILoggingBuilder"/>.</param>
        /// <returns>The same instance of the <see cref="IClientBuilder"/> for chaining.</returns>
        public static IClientBuilder ConfigureLogging(this IClientBuilder builder, Action<ILoggingBuilder> configureLogging)
        {
            return builder.ConfigureServices(collection => collection.AddLogging(loggingBuilder => configureLogging(loggingBuilder)));
        }

        /// <summary>
        /// Configure the client to use <see cref="StaticGatewayListProvider"/>
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IClientBuilder UseStaticGatewayListProvider(this IClientBuilder builder, Action<StaticGatewayListProviderOptions> configureOptions)
        {
            return builder.ConfigureServices(collection =>
                collection.UseStaticGatewayListProvider(configureOptions));
        }

        /// <summary>
        /// Configure the client to use <see cref="StaticGatewayListProvider"/>
        /// </summary>
        public static IClientBuilder UseStaticGatewayListProvider(this IClientBuilder builder, Action<OptionsBuilder<StaticGatewayListProviderOptions>> configureOptions)
        {
            return builder.ConfigureServices(collection =>
                collection.UseStaticGatewayListProvider(configureOptions));
        }

        /// <summary>
        /// Returns the <see cref="ApplicationPartManager"/> for this builder.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <returns>The <see cref="ApplicationPartManager"/> for this builder.</returns>
        public static IApplicationPartManager GetApplicationPartManager(this IClientBuilder builder) => ApplicationPartManagerExtensions.GetApplicationPartManager(builder.Properties);
        
        /// <summary>
        /// Configures the <see cref="ApplicationPartManager"/> for this builder.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configure">The configuration delegate.</param>
        /// <returns>The builder.</returns>
        public static IClientBuilder ConfigureApplicationParts(this IClientBuilder builder, Action<IApplicationPartManager> configure)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }

            configure(builder.GetApplicationPartManager());
            return builder;
        }

        /// <summary>
        /// Configures the cluster client general options.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configureOptions">The delegate that configures the options.</param>
        /// <returns>The same instance of the <see cref="IClientBuilder"/> for chaining.</returns>
        public static IClientBuilder ConfigureClusterClient(this IClientBuilder builder, Action<ClusterClientOptions> configureOptions)
        {
            if (configureOptions != null)
            {
                builder.ConfigureServices(services => services.Configure<ClusterClientOptions>(configureOptions));
            }

            return builder;
        }

        /// <summary>
        /// Configures the cluster client general options.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configureOptions">The delegate that configures the options using the options builder.</param>
        /// <returns>The same instance of the <see cref="IClientBuilder"/> for chaining.</returns>
        public static IClientBuilder ConfigureClusterClient(this IClientBuilder builder, Action<OptionsBuilder<ClusterClientOptions>> configureOptions)
        {
            if (configureOptions != null)
            {
                builder.ConfigureServices(services =>
                {
                    configureOptions.Invoke(services.AddOptions<ClusterClientOptions>());
                });
            }

            return builder;
        }
    }
}