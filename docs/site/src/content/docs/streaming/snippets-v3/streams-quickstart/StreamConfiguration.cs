using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace StreamsQuickstart;

internal static class StreamConfiguration
{
    public static void ConfigureSilo()
    {
        // <silo_sms_provider>
        hostBuilder.AddSimpleMessageStreamProvider("SMSProvider")
                   .AddMemoryGrainStorage("PubSubStore");
        // </silo_sms_provider>
    }

    public static void ConfigureClient()
    {
        // <client_sms_provider>
        clientBuilder.AddSimpleMessageStreamProvider("SMSProvider");
        // </client_sms_provider>
    }

    public static void ConfigureSiloWithOptions()
    {
        // <silo_sms_provider_immutable>
        siloBuilder
            .AddSimpleMessageStreamProvider(
                "SMSProvider",
                options => options.OptimizeForImmutableData = false);
        // </silo_sms_provider_immutable>
    }
}

// Stub types for compilation
internal static class hostBuilder
{
    public static ISiloHostBuilder AddSimpleMessageStreamProvider(string name) => null!;
}

internal static class clientBuilder
{
    public static IClientBuilder AddSimpleMessageStreamProvider(string name) => null!;
}

internal static class siloBuilder
{
    public static ISiloHostBuilder AddSimpleMessageStreamProvider(string name, Action<SimpleMessageStreamProviderOptions> options) => null!;
}
