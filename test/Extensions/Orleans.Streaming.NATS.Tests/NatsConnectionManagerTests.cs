using Orleans.Streaming.NATS;
using TestExtensions;
using Xunit;

namespace NATS.Tests;

[TestSuite("BVT")]
[TestArea("Streaming")]
[TestProvider("NATS")]
[TestCategory("NATS")]
public sealed class NatsConnectionManagerTests
{
    [Theory]
    [InlineData("nats://user:password@first.example:4222", "nats://first.example:4222/")]
    [InlineData("nats://sample-token@first.example:4222", "nats://first.example:4222/")]
    [InlineData("tls://user:password@first.example:4443", "tls://first.example:4443/")]
    [InlineData("nats://user%40example:pass%3Aword@first.example:4222", "nats://first.example:4222/")]
    public void LogSafeServerDescription_RedactsCredentials(string connectionUrl, string expected)
    {
        var uri = new Uri(connectionUrl, UriKind.Absolute);
        Assert.NotEmpty(uri.UserInfo);

        var description = NatsConnectionManager.GetLogSafeServerDescription(connectionUrl);

        Assert.Equal(expected, description);
    }

    [Fact]
    public void LogSafeServerDescription_RedactsCredentialsFromEverySeedEndpoint()
    {
        const string connectionUrls =
            "nats://user:password@first.example:4222,nats://sample-token@second.example:4223";
        foreach (var endpoint in connectionUrls.Split(','))
        {
            var uri = new Uri(endpoint, UriKind.Absolute);
            Assert.NotEmpty(uri.UserInfo);
        }

        var description = NatsConnectionManager.GetLogSafeServerDescription(connectionUrls);

        Assert.Equal("nats://first.example:4222/,nats://second.example:4223/", description);
    }

    [Theory]
    [InlineData("nats://first.example:4222", "nats://first.example:4222/")]
    [InlineData("nats://[::1]:4222", "nats://[::1]:4222/")]
    [InlineData(
        " , nats://user:password@first.example:4222, , nats://second.example:4223, ",
        "nats://first.example:4222/,nats://second.example:4223/")]
    public void LogSafeServerDescription_PreservesEndpoints(string connectionUrls, string expected)
    {
        var description = NatsConnectionManager.GetLogSafeServerDescription(connectionUrls);

        Assert.Equal(expected, description);
    }

    [Theory]
    [InlineData("nats://user:password@")]
    [InlineData("nats://user:password@first.example:invalid-port")]
    public void LogSafeServerDescription_UsesPlaceholderForInvalidEndpoints(string connectionUrl)
    {
        Assert.False(Uri.TryCreate(connectionUrl, UriKind.Absolute, out _));

        var description = NatsConnectionManager.GetLogSafeServerDescription(connectionUrl);

        Assert.Equal("configured NATS endpoint", description);
    }
}
