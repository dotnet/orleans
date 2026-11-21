using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Orleans.AdvancedReminders.Redis;
using Xunit;

namespace Tester.Redis.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
public class ReminderTableExpiryTests
{
    [Fact]
    public void DefaultExpiry_NeverExpiresTheTable()
    {
        var options = new RedisReminderTableOptions();

        Assert.Same(ReminderTableExpiry.Never, options.TableExpiry);
        Assert.False(options.TableExpiry.TryGetInactivityPeriod(out _));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void AfterInactivity_RejectsNonPositivePeriods(long ticks)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => ReminderTableExpiry.AfterInactivity(TimeSpan.FromTicks(ticks)));

        Assert.Equal("inactivityPeriod", exception.ParamName);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    public void AfterInactivity_PreservesPositivePeriods(long ticks)
    {
        var period = TimeSpan.FromTicks(ticks);
        var policy = ReminderTableExpiry.AfterInactivity(period);

        Assert.True(policy.TryGetInactivityPeriod(out var actual));
        Assert.Equal(period, actual);
    }

    [Theory]
    [InlineData("Never", "Never")]
    [InlineData("AfterInactivity(01:00:00)", "AfterInactivity(01:00:00)")]
    [InlineData(" afterinactivity( 1.00:00:00 ) ", "AfterInactivity(1.00:00:00)")]
    [InlineData("AfterInactivity(00:00:00.0000001)", "AfterInactivity(00:00:00.0000001)")]
    public void ConfigurationBinding_RoundTripsExpiry(string configured, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [nameof(RedisReminderTableOptions.TableExpiry)] = configured,
        }).Build();
        var options = new RedisReminderTableOptions();

        configuration.Bind(options);

        Assert.Equal(expected, options.TableExpiry.ToString());
        Assert.Equal(expected, TypeDescriptor.GetConverter(typeof(ReminderTableExpiry)).ConvertTo(
            null, CultureInfo.GetCultureInfo("ar-SA"), options.TableExpiry, typeof(string)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("AfterInactivity(00:00:00)")]
    [InlineData("AfterInactivity(-00:00:01)")]
    [InlineData("AfterInactivity()")]
    [InlineData("AfterInactivity(unknown)")]
    [InlineData("Never(1)")]
    [InlineData("")]
    public void ConfigurationBinding_RejectsInvalidExpiry(string configured)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [nameof(RedisReminderTableOptions.TableExpiry)] = configured,
        }).Build();

        Assert.Throws<InvalidOperationException>(() => configuration.Bind(new RedisReminderTableOptions()));
    }
}
