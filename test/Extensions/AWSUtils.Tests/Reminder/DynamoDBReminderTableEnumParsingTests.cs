using System.Collections.Generic;
using System.Reflection;
using Amazon.DynamoDBv2.Model;
using Orleans.Reminders.DynamoDB;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace AWSUtils.Tests.RemindersTest;

[TestCategory("Reminders"), TestCategory("AWS"), TestCategory("DynamoDb")]
public class DynamoDBReminderTableEnumParsingTests
{
    [Fact]
    public void ReadPriority_ReturnsNormal_WhenMissing()
    {
        var value = InvokeReadPriority(new Dictionary<string, AttributeValue>());
        Assert.Equal(ReminderPriority.Normal, value);
    }

    [Fact]
    public void ReadAction_ReturnsSkip_WhenMissing()
    {
        var value = InvokeReadAction(new Dictionary<string, AttributeValue>());
        Assert.Equal(MissedReminderAction.Skip, value);
    }

    [Fact]
    public void ReadPriority_ReturnsNormal_WhenInvalid()
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["Priority"] = new AttributeValue { N = "999" }
        };

        var value = InvokeReadPriority(item);
        Assert.Equal(ReminderPriority.Normal, value);
    }

    [Fact]
    public void ReadAction_ReturnsSkip_WhenInvalid()
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["Action"] = new AttributeValue { N = "-3" }
        };

        var value = InvokeReadAction(item);
        Assert.Equal(MissedReminderAction.Skip, value);
    }

    [Theory]
    [InlineData((int)ReminderPriority.High, ReminderPriority.High)]
    [InlineData((int)ReminderPriority.Normal, ReminderPriority.Normal)]
    public void ReadPriority_ReturnsExpectedValue_WhenValid(int rawValue, ReminderPriority expected)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["Priority"] = new AttributeValue { N = rawValue.ToString() }
        };

        var value = InvokeReadPriority(item);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData((int)MissedReminderAction.FireImmediately, MissedReminderAction.FireImmediately)]
    [InlineData((int)MissedReminderAction.Skip, MissedReminderAction.Skip)]
    [InlineData((int)MissedReminderAction.Notify, MissedReminderAction.Notify)]
    public void ReadAction_ReturnsExpectedValue_WhenValid(int rawValue, MissedReminderAction expected)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["Action"] = new AttributeValue { N = rawValue.ToString() }
        };

        var value = InvokeReadAction(item);
        Assert.Equal(expected, value);
    }

    private static ReminderPriority InvokeReadPriority(Dictionary<string, AttributeValue> item)
    {
        var method = typeof(DynamoDBReminderTable).GetMethod("ReadPriority", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { item });
        Assert.NotNull(result);
        return (ReminderPriority)result!;
    }

    private static MissedReminderAction InvokeReadAction(Dictionary<string, AttributeValue> item)
    {
        var method = typeof(DynamoDBReminderTable).GetMethod("ReadAction", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { item });
        Assert.NotNull(result);
        return (MissedReminderAction)result!;
    }
}
