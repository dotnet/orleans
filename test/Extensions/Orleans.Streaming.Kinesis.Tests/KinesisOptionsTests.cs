using System.Reflection;
using Orleans.Runtime;
using Orleans.Streaming.Kinesis;
using TestExtensions;
using Xunit;

namespace Orleans.Streaming.Kinesis.Tests;

[TestSuite("BVT")]
[TestArea("Streaming")]
[TestProvider("Kinesis")]
[TestCategory("AWS"), TestCategory("Kinesis")]
public sealed class KinesisOptionsTests
{
    [Fact]
    public void ConnectionStringIsRedacted()
    {
        var property = typeof(KinesisStreamOptions).GetProperty(nameof(KinesisStreamOptions.ConnectionString));
        var redaction = property?.GetCustomAttribute<RedactAttribute>();

        Assert.NotNull(redaction);
        var redacted = redaction.Redact("https://localhost:4566;access-key;secret-key;us-east-1")?.ToString();
        Assert.DoesNotContain("access-key", redacted);
        Assert.DoesNotContain("secret-key", redacted);
    }

    [Fact]
    public void InvalidConnectionStringDoesNotExposeValue()
    {
        const string value = "service;access-key;secret-key";
        var options = new KinesisStreamOptions();

        var exception = Assert.Throws<ArgumentException>(() => options.ConnectionString = value);

        Assert.DoesNotContain(value, exception.Message);
        Assert.DoesNotContain("secret-key", exception.Message);
    }

    [Fact]
    public void ValidatorAcceptsDefaultCredentials()
    {
        var options = new KinesisStreamOptions
        {
            Service = "https://kinesis.us-west-2.amazonaws.com",
            Region = "us-west-2",
        };

        new KinesisStreamOptionsValidator(options, "Kinesis").ValidateConfiguration();
    }

    [Fact]
    public void ValidatorRejectsUnpairedCredentials()
    {
        var options = new KinesisStreamOptions
        {
            AccessKey = "access-key",
        };

        var exception = Assert.Throws<OrleansConfigurationException>(
            () => new KinesisStreamOptionsValidator(options, "Kinesis").ValidateConfiguration());

        Assert.DoesNotContain("access-key", exception.Message);
    }

    [Fact]
    public void ValidatorRejectsUnsafePollingInterval()
    {
        var options = new KinesisStreamOptions
        {
            GetRecordsInterval = TimeSpan.FromMilliseconds(199),
        };

        Assert.Throws<OrleansConfigurationException>(
            () => new KinesisStreamOptionsValidator(options, "Kinesis").ValidateConfiguration());
    }
}
