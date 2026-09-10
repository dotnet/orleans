using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Orleans.AdvancedReminders.DynamoDB;
using Xunit;

namespace AWSUtils.Tests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
public class DynamoDBCapacityTests
{
    [Fact]
    public void DefaultCapacity_RemainsProvisionedTenReadAndFiveWriteUnits()
    {
        var options = new DynamoDBReminderStorageOptions();

        Assert.True(options.Capacity.TryGetProvisionedThroughput(out var readUnits, out var writeUnits));
        Assert.Equal(10, readUnits);
        Assert.Equal(5, writeUnits);
        Assert.False(DynamoDBCapacity.OnDemand.TryGetProvisionedThroughput(out _, out _));
    }

    [Theory]
    [InlineData(0, 1, "readUnits")]
    [InlineData(-1, 1, "readUnits")]
    [InlineData(int.MinValue, 1, "readUnits")]
    [InlineData(1, 0, "writeUnits")]
    [InlineData(1, -1, "writeUnits")]
    public void Provisioned_RejectsNonPositiveCapacity(int readUnits, int writeUnits, string parameterName)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => DynamoDBCapacity.Provisioned(readUnits, writeUnits));

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Theory]
    [InlineData("OnDemand", "OnDemand")]
    [InlineData("Provisioned(7,9)", "Provisioned(7,9)")]
    [InlineData(" provisioned( 1, 3 ) ", "Provisioned(1,3)")]
    [InlineData("Provisioned(2147483647,1)", "Provisioned(2147483647,1)")]
    public void ConfigurationBinding_RoundTripsCapacity(string configured, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [nameof(DynamoDBReminderStorageOptions.Capacity)] = configured,
        }).Build();
        var options = new DynamoDBReminderStorageOptions();

        configuration.Bind(options);

        Assert.Equal(expected, options.Capacity.ToString());
        Assert.Equal(expected, TypeDescriptor.GetConverter(typeof(DynamoDBCapacity)).ConvertTo(
            null, CultureInfo.GetCultureInfo("ar-SA"), options.Capacity, typeof(string)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("Provisioned(0,1)")]
    [InlineData("Provisioned(1,-1)")]
    [InlineData("Provisioned(2147483648,1)")]
    [InlineData("Provisioned(1,2,3)")]
    [InlineData("Provisioned(1)")]
    [InlineData("OnDemand(1,2)")]
    [InlineData("")]
    public void ConfigurationBinding_RejectsInvalidCapacity(string configured)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [nameof(DynamoDBReminderStorageOptions.Capacity)] = configured,
        }).Build();

        Assert.Throws<InvalidOperationException>(() => configuration.Bind(new DynamoDBReminderStorageOptions()));
    }

    [Fact]
    public void OptionsValidator_RejectsMissingCapacity()
    {
        var options = new DynamoDBReminderStorageOptions { Capacity = null! };
        var validator = new DynamoDBReminderStorageOptionsValidator(Options.Create(options));

        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }
}
