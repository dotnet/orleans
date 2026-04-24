using StackExchange.Redis;
using TestExtensions;

namespace Tester.Redis.Streaming;

internal static class RedisStreamTestUtils
{
    public static ConfigurationOptions GetConfigurationOptions() => ConfigurationOptions.Parse(TestDefaultConfiguration.RedisConnectionString);

    public static async Task DeleteServiceKeysAsync(string serviceId)
    {
        if (string.IsNullOrWhiteSpace(serviceId) || string.IsNullOrWhiteSpace(TestDefaultConfiguration.RedisConnectionString))
        {
            return;
        }

        using var connection = await ConnectionMultiplexer.ConnectAsync(GetConfigurationOptions());
        var database = connection.GetDatabase();
        foreach (var server in connection.GetServers())
        {
            await foreach (var key in server.KeysAsync(pattern: $"{serviceId}/*"))
            {
                await database.KeyDeleteAsync(key);
            }
        }
    }
}
