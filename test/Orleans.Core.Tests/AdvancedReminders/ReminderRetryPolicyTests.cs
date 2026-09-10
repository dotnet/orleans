using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Orleans.AdvancedReminders;
using Xunit;
using AdvancedReminderOptions = Orleans.AdvancedReminders.ReminderOptions;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderRetryPolicyTests
{
    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    public void Limited_RejectsNonPositiveAttemptCounts(int maximumAttempts)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => ReminderRetryPolicy.Limited(maximumAttempts));
        Assert.Equal("maximumAttempts", exception.ParamName);
    }

    [Fact]
    public void NamedPolicies_HaveDistinctFailureSemantics()
    {
        Assert.False(ReminderRetryPolicy.NoRetries.RetainsFailedDeliveries);
        Assert.False(ReminderRetryPolicy.NoRetries.TryGetAttemptLimit(out _));
        Assert.True(ReminderRetryPolicy.UntilSuccess.RetainsFailedDeliveries);
        Assert.False(ReminderRetryPolicy.UntilSuccess.TryGetAttemptLimit(out _));
        var limited = ReminderRetryPolicy.Limited(1);
        Assert.False(limited.RetainsFailedDeliveries);
        Assert.True(limited.TryGetAttemptLimit(out var limit));
        Assert.Equal(1, limit);
    }

    [Theory]
    [InlineData("NoRetries", "NoRetries")]
    [InlineData("UntilSuccess", "UntilSuccess")]
    [InlineData("Limited(1)", "Limited(1)")]
    [InlineData("Limited(3)", "Limited(3)")]
    [InlineData("Limited(2147483647)", "Limited(2147483647)")]
    [InlineData(" limited( 3 ) ", "Limited(3)")]
    public void ConfigurationBinding_RoundTripsNamedPolicies(string configured, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [nameof(AdvancedReminderOptions.DeliveryRetryPolicy)] = configured,
        }).Build();
        var options = new AdvancedReminderOptions();

        configuration.Bind(options);

        Assert.Equal(expected, options.DeliveryRetryPolicy.ToString());
        var converter = TypeDescriptor.GetConverter(typeof(ReminderRetryPolicy));
        Assert.Equal(expected, converter.ConvertTo(null, CultureInfo.GetCultureInfo("ar-SA"), options.DeliveryRetryPolicy, typeof(string)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("Limited(0)")]
    [InlineData("Limited(-1)")]
    [InlineData("Limited(2147483648)")]
    [InlineData("Limited()")]
    [InlineData("Limited(3")]
    [InlineData("NoRetries(3)")]
    [InlineData("Unknown")]
    public void ConfigurationBinding_RejectsInvalidPolicies(string configured)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [nameof(AdvancedReminderOptions.DeliveryRetryPolicy)] = configured,
        }).Build();

        Assert.Throws<InvalidOperationException>(() => configuration.Bind(new AdvancedReminderOptions()));
    }
}
