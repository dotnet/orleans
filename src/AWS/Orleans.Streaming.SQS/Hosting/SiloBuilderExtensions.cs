using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using OrleansAWSUtils.Streams;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use SQS persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddSqsStreams(this ISiloHostBuilder builder, string name, Action<SqsStreamOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddSiloSqsStreams(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use SQS persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddSqsStreams(this ISiloHostBuilder builder, string name, Action<OptionsBuilder<SqsStreamOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddSiloSqsStreams(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use SQS persistent streams.
        /// </summary>
        public static IServiceCollection AddSiloSqsStreams(this IServiceCollection services, string name, Action<SqsStreamOptions> configureOptions)
        {
            return services.AddSiloSqsStreams(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use SQS persistent streams.
        /// </summary>
        public static IServiceCollection AddSiloSqsStreams(this IServiceCollection services, string name,
            Action<OptionsBuilder<SqsStreamOptions>> configureOptions = null)
        {
            return services.ConfigureNamedOptionForLogging<SqsStreamOptions>(name)
                           .AddSiloPersistentStreams<SqsStreamOptions>(name, SQSAdapterFactory.Create, configureOptions);
        }
    }
}
