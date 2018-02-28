
using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using OrleansAWSUtils.Streams;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configure cluster client to use SQS persistent streams.
        /// </summary>
        public static IClientBuilder AddSqsStreams(this IClientBuilder builder, string name, Action<SqsStreamOptions> configureOptions)
        {
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(SQSAdapterFactory).Assembly))
                .ConfigureServices(services => services.AddClusterClientSqsStreams(name, configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use SQS persistent streams.
        /// </summary>
        public static IClientBuilder AddSqsStreams(this IClientBuilder builder, string name, Action<OptionsBuilder<SqsStreamOptions>> configureOptions = null)
        {
            return builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(SQSAdapterFactory).Assembly))
                .ConfigureServices(services => services.AddClusterClientSqsStreams(name, configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use SQS persistent streams.
        /// </summary>
        private static void AddClusterClientSqsStreams(this IServiceCollection services, string name, Action<SqsStreamOptions> configureOptions)
        {
            services.AddClusterClientSqsStreams(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use SQS persistent streams.
        /// </summary>
        private static void AddClusterClientSqsStreams(this IServiceCollection services, string name,
            Action<OptionsBuilder<SqsStreamOptions>> configureOptions = null)
        {
            services.ConfigureNamedOptionForLogging<SqsStreamOptions>(name)
                           .AddClusterClientPersistentStreams<SqsStreamOptions>(name, SQSAdapterFactory.Create, configureOptions);
        }
    }
}
