#nullable enable
using System.Collections.Generic;
using System.Reflection;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Orleans.AdvancedReminders.DynamoDB;
using Xunit;
using DurableJobPriority = Orleans.DurableJobs.DurableJobPriority;
using MissedReminderAction = Orleans.AdvancedReminders.Runtime.MissedReminderAction;

namespace AWSUtils.Tests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders"), TestCategory("AWS"), TestCategory("DynamoDb")]
public class DynamoDBReminderTableEnumParsingTests
{
    [Fact]
    public void ReadPriority_ReturnsNormal_WhenMissing()
    {
        var value = InvokeReadPriority(new Dictionary<string, AttributeValue>());
        Assert.Equal(DurableJobPriority.Normal, value);
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
            ["Priority"] = new AttributeValue { N = "999" },
        };

        var value = InvokeReadPriority(item);
        Assert.Equal(DurableJobPriority.Normal, value);
    }

    [Fact]
    public void ReadAction_ReturnsSkip_WhenInvalid()
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["Action"] = new AttributeValue { N = "-3" },
        };

        var value = InvokeReadAction(item);
        Assert.Equal(MissedReminderAction.Skip, value);
    }

    [Theory]
    [InlineData((int)DurableJobPriority.Low, DurableJobPriority.Low)]
    [InlineData((int)DurableJobPriority.High, DurableJobPriority.High)]
    [InlineData((int)DurableJobPriority.Normal, DurableJobPriority.Normal)]
    public void ReadPriority_ReturnsExpectedValue_WhenValid(int rawValue, DurableJobPriority expected)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["Priority"] = new AttributeValue { N = rawValue.ToString() },
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
            ["Action"] = new AttributeValue { N = rawValue.ToString() },
        };

        var value = InvokeReadAction(item);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("42", true)]
    [InlineData("missing-etag", false)]
    [InlineData(" 42 ", false)]
    [InlineData("+42", false)]
    [InlineData("042", false)]
    public void TryCreateETagValue_AcceptsOnlySupportedStorageFormats(string eTag, bool expected)
    {
        var result = DynamoDBReminderTable.TryCreateETagValue(eTag, out var value);

        Assert.Equal(expected, result);
        Assert.Equal(expected, value is not null);
    }

    [Fact]
    public void ConstructReminderId_UsesUnambiguousStructuredIdentity()
    {
        var grainId = GrainId.Create("test", "G");

        var first = DynamoDBReminderTable.ConstructReminderId("a", grainId, "G_b");
        var second = DynamoDBReminderTable.ConstructReminderId("a_G", grainId, "b");

        Assert.NotEqual(first, second);
        Assert.Equal(64, first.Length);
        Assert.Equal(64, second.Length);
    }

    [Fact]
    public void GetAttributeDefinitionsForIndex_ReturnsOnlyIndexKeyAttributes()
    {
        var attributes = new List<AttributeDefinition>
        {
            new("ServiceId", ScalarAttributeType.S),
            new("GrainHash", ScalarAttributeType.N),
            new("GrainReference", ScalarAttributeType.S),
            new("ReminderId", ScalarAttributeType.S),
        };
        var index = new GlobalSecondaryIndex
        {
            KeySchema =
            [
                new KeySchemaElement("ServiceId", KeyType.HASH),
                new KeySchemaElement("GrainHash", KeyType.RANGE),
            ],
        };

        var result = DynamoDBStorage.GetAttributeDefinitionsForIndex(attributes, index);

        Assert.Equal(["ServiceId", "GrainHash"], result.Select(attribute => attribute.AttributeName));
    }

    [Fact]
    public void WrappedRangePhaseContinuation_MakesLowerRangeReachableAfterExactFullPage()
    {
        var continuationToken = DynamoDBReminderTable.CreatePhaseContinuationToken(phase: 1);

        Assert.Equal(1, DynamoDBReminderTable.GetContinuationPhase(continuationToken));
    }

    private static DurableJobPriority InvokeReadPriority(Dictionary<string, AttributeValue> item)
    {
        var method = typeof(DynamoDBReminderTable).GetMethod("ReadPriority", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [item]);
        Assert.NotNull(result);
        return (DurableJobPriority)result!;
    }

    private static MissedReminderAction InvokeReadAction(Dictionary<string, AttributeValue> item)
    {
        var method = typeof(DynamoDBReminderTable).GetMethod("ReadAction", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [item]);
        Assert.NotNull(result);
        return (MissedReminderAction)result!;
    }
}
