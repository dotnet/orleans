using Microsoft.Extensions.Hosting;

namespace Orleans.Docs.Snippets.Aspire.Client;

// This class contains example code for Orleans client configuration with Aspire
public static class ClientProgram
{
    // <kinesis_streaming_client>
    public static void KinesisStreaming(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.AddServiceDefaults();
        builder.UseOrleansClient();

        builder.Build().Run();
    }
    // </kinesis_streaming_client>

    // <client_basic_config>
    public static void BasicClientConfiguration(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.AddServiceDefaults();
        builder.AddKeyedRedisClient("orleans-redis");

        // Configure Orleans client - Aspire injects clustering configuration automatically
        builder.UseOrleansClient();

        builder.Build().Run();
    }
    // </client_basic_config>
}
