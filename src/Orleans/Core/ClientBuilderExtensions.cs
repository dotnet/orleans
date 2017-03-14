using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Runtime.Configuration;

namespace Orleans
{
    /// <summary>
    /// Extension methods for <see cref="IClientBuilder"/>.
    /// </summary>
    public static class ClientBuilderExtensions
    {
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
    }
}