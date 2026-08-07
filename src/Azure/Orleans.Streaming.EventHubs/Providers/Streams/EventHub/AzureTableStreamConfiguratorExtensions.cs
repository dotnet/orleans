using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extension methods for configuring Azure Table stream checkpointers.
    /// </summary>
    public static class AzureTableStreamConfiguratorExtensions
    {
        /// <summary>
        /// Configures the stream provider to persist checkpoints using Azure Table Storage.
        /// </summary>
        /// <param name="configurator">The configuration builder.</param>
        /// <param name="configureOptions">The Azure Table checkpointer configuration.</param>
        public static void UseAzureTableCheckpointer(
            this ISiloPersistentStreamConfigurator configurator,
            Action<OptionsBuilder<AzureTableStreamCheckpointerOptions>> configureOptions)
        {
            configurator.ConfigureDelegate(services =>
                services.AddTransient<IConfigurationValidator>(sp =>
                    new AzureTableStreamCheckpointerOptionsValidator(
                        sp.GetRequiredService<IOptionsMonitor<AzureTableStreamCheckpointerOptions>>().Get(configurator.Name),
                        configurator.Name)));
            configurator.ConfigureComponent<AzureTableStreamCheckpointerOptions, IStreamQueueCheckpointerFactory>(
                AzureTableStreamQueueCheckpointerFactory.CreateFactory,
                options =>
                {
                    options.Validate(
                        static value => value.PersistInterval > TimeSpan.Zero,
                        $"{nameof(AzureTableStreamCheckpointerOptions.PersistInterval)} must be greater than zero.");
                    configureOptions(options);
                });
        }
    }
}
