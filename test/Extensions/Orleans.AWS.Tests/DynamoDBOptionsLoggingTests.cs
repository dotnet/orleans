using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Xunit;

namespace AWSUtils.Tests;

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Configuration")]
[TestCategory("BVT")]
[TestCategory("AWS")]
[TestCategory("DynamoDB")]
public sealed class DynamoDBOptionsLoggingTests
{
    private const string AccessKey = "fake-access-key";
    private const string SecretKey = "fake-secret-key";
    private const string Token = "fake-session-token";
    private const string Service = "us-west-2";
    private const string ProfileName = "diagnostic-profile";

    [Fact]
    public void LogClusteringOptions_RedactsCredentialsAndPreservesSafeFields()
    {
        AssertLoggedOptions<DynamoDBClusteringOptions>(options =>
        {
            options.AccessKey = AccessKey;
            options.SecretKey = SecretKey;
            options.Token = Token;
            options.Service = Service;
            options.ProfileName = ProfileName;
        });
    }

    [Fact]
    public void LogGatewayOptions_RedactsCredentialsAndPreservesSafeFields()
    {
        AssertLoggedOptions<DynamoDBGatewayOptions>(options =>
        {
            options.AccessKey = AccessKey;
            options.SecretKey = SecretKey;
            options.Token = Token;
            options.Service = Service;
            options.ProfileName = ProfileName;
        });
    }

    [Fact]
    public void LogNamedStorageOptions_RedactsCredentialsAndPreservesSafeFields()
    {
        AssertLoggedOptions<DynamoDBStorageOptions>(options =>
        {
            options.AccessKey = AccessKey;
            options.SecretKey = SecretKey;
            options.Token = Token;
            options.Service = Service;
            options.ProfileName = ProfileName;
        }, "grain-storage");
    }

    [Fact]
    public void LogReminderOptions_RedactsCredentialsAndPreservesSafeFields()
    {
        AssertLoggedOptions<DynamoDBReminderStorageOptions>(options =>
        {
            options.AccessKey = AccessKey;
            options.SecretKey = SecretKey;
            options.Token = Token;
            options.Service = Service;
            options.ProfileName = ProfileName;
        });
    }

    [Fact]
    public void LogNamedTransactionalStorageOptions_RedactsCredentialsAndPreservesSafeFields()
    {
        AssertLoggedOptions<DynamoDBTransactionalStorageOptions>(options =>
        {
            options.AccessKey = AccessKey;
            options.SecretKey = SecretKey;
            options.Token = Token;
            options.Service = Service;
            options.ProfileName = ProfileName;
        }, "transactional-storage");
    }

    private static void AssertLoggedOptions<TOptions>(Action<TOptions> configure, string? name = null)
        where TOptions : class, new()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(IOptionFormatter<>), typeof(DefaultOptionsFormatter<>));
        services.AddSingleton(typeof(IOptionFormatterResolver<>), typeof(DefaultOptionsFormatterResolver<>));
        if (name is null)
        {
            services.Configure(configure);
            services.ConfigureFormatter<TOptions>();
        }
        else
        {
            services.Configure(name, configure);
            services.ConfigureNamedOptionForLogging<TOptions>(name);
        }

        using var provider = services.BuildServiceProvider();
        Assert.IsAssignableFrom<IOptionFormatter<TOptions>>(Assert.Single(provider.GetServices<IOptionFormatter>()));
        var logger = new CapturingLogger();

        new TestOptionsLogger(logger, provider).LogOptions();

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains(typeof(TOptions).FullName!, entry.Message);
        if (name is not null)
        {
            Assert.Contains(name, entry.Message);
        }

        var lines = entry.Message.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains($"Service: {Service}", lines);
        Assert.Contains($"ProfileName: {ProfileName}", lines);
        Assert.Contains("AccessKey: REDACTED", lines);
        Assert.Contains("SecretKey: REDACTED", lines);
        Assert.DoesNotContain(AccessKey, entry.Message);
        Assert.DoesNotContain(SecretKey, entry.Message);
        Assert.Contains("Token: REDACTED", lines);
        Assert.DoesNotContain(Token, entry.Message);
    }

    private sealed class TestOptionsLogger(ILogger logger, IServiceProvider services) : OptionsLogger(logger, services);

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
