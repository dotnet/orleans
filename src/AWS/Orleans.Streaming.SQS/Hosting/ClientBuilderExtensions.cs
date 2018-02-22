
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
            return builder.ConfigureServices(services => services.AddClusterClientSqsStreams(name, configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use SQS persistent streams.
        /// </summary>
        public static IClientBuilder AddSqsStreams(this IClientBuilder builder, string name, Action<OptionsBuilder<SqsStreamOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddClusterClientSqsStreams(name, configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use SQS persistent streams.
        /// </summary>
        public static IServiceCollection AddClusterClientSqsStreams(this IServiceCollection services, string name, Action<SqsStreamOptions> configureOptions)
        {
            return services.AddClusterClientSqsStreams(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure cluster client to use SQS persistent streams.
        /// </summary>
        public static IServiceCollection AddClusterClientSqsStreams(this IServiceCollection services, string name,
            Action<OptionsBuilder<SqsStreamOptions>> configureOptions = null)
        {
            return services.ConfigureNamedOptionForLogging<SqsStreamOptions>(name)
                           .AddClusterClientPersistentStreams<SqsStreamOptions>(name, SQSAdapterFactory.Create, configureOptions);
        }
    }
}
