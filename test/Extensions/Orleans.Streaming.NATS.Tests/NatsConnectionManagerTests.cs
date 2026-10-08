using System.Text;
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
    [InlineData("chat", false, "chat")]
    [InlineData("key/slash", false, "key/slash")]
    [InlineData("caf\u00e9", false, "caf\u00e9")]
    [InlineData("", true, "null")]
    [InlineData("null", false, "null")]
    [InlineData("null", true, "~6E756C6C")]
    [InlineData("chat.events", true, "~636861742E6576656E7473")]
    [InlineData("key.with.dots", false, "~6B65792E776974682E646F7473")]
    [InlineData("*", false, "~2A")]
    [InlineData(">", false, "~3E")]
    [InlineData(" \t\r\n", false, "~20090D0A")]
    [InlineData("\0", false, "~00")]
    [InlineData("~2E", false, "~7E3245")]
    public void SubjectTokens_PreserveLegacyTokensAndEncodeReservedValues(string value, bool isNamespace, string expected)
    {
        var token = NatsConnectionManager.GetSubjectToken(Encoding.UTF8.GetBytes(value), isNamespace);

        Assert.Equal(expected, token);
    }

    [Fact]
    public void SubjectTokens_PreserveEveryByteWithoutCollisions()
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        for (var value = 0; value <= byte.MaxValue; value++)
        {
            byte[] bytes = [(byte)value];
            var token = NatsConnectionManager.GetSubjectToken(bytes);

            Assert.NotEmpty(token);
            Assert.DoesNotContain(token, character => character is '.' or '*' or '>' || char.IsWhiteSpace(character) || char.IsControl(character));
            Assert.True(tokens.Add(token), $"Duplicate subject token for byte {value}");
            var decoded = token.StartsWith('~') ? Convert.FromHexString(token[1..]) : Encoding.UTF8.GetBytes(token);
            Assert.Equal(bytes, decoded);
        }

        Assert.Equal(256, tokens.Count);
    }

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
