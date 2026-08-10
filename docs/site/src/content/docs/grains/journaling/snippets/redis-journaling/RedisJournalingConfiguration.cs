using Microsoft.Extensions.Hosting;
using Orleans.Journaling;
using StackExchange.Redis;

namespace Orleans.Docs.Snippets.Journaling;

public static class RedisJournalingConfiguration
{
    public static void ConfigureRedisJournaling()
    {
        // <configure_redis_journaling>
        var builder = Host.CreateApplicationBuilder();
        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.AddRedisJournalStorage(options =>
            {
                options.ConfigurationOptions = new ConfigurationOptions
                {
                    EndPoints = { "localhost:6379" },
                    AbortOnConnectFail = false
                };
            });
        });

        using var host = builder.Build();
        // </configure_redis_journaling>
    }
}
