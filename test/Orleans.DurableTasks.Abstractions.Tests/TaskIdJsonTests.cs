using System.Text.Json;
using System.Text.Json.Serialization;
using CsCheck;
using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public sealed class TaskIdJsonTests
{
    [Fact]
    public void TaskIdsRoundTripAsTheirCanonicalStringsWithoutRegistration()
    {
        var segment = Gen.Char["ab/\\:$"].Array[1, 6].Select(static characters => new string(characters));
        segment.Array[1, 4].Sample(
            segments =>
            {
                var taskId = TaskId.CreateRoot(segments[0]);
                foreach (var child in segments.AsSpan(1))
                {
                    taskId = taskId.Child(child);
                }

                var holder = new TaskIdHolder(taskId, taskId, new() { [taskId] = 1 });
                var reflected = JsonSerializer.Serialize(holder);
                var generated = JsonSerializer.Serialize(holder, TaskIdJsonTestContext.Default.TaskIdHolder);

                // Both serializers write the identifier's canonical string, as a value and as a property name.
                var canonical = JsonSerializer.Serialize(taskId.ToString());
                Assert.Equal("{\"Id\":" + canonical + ",\"Optional\":" + canonical + ",\"Counts\":{" + canonical + ":1}}", reflected);
                Assert.Equal(reflected, generated);
                Assert.Equivalent(holder, JsonSerializer.Deserialize<TaskIdHolder>(reflected));
                Assert.Equivalent(holder, JsonSerializer.Deserialize(generated, TaskIdJsonTestContext.Default.TaskIdHolder));
            });
    }

    [Fact]
    public void EmptyAndMissingTaskIdsRoundTrip()
    {
        var holder = new TaskIdHolder(TaskId.None, null, []);

        var json = JsonSerializer.Serialize(holder, TaskIdJsonTestContext.Default.TaskIdHolder);

        Assert.Equal("""{"Id":"","Optional":null,"Counts":{}}""", json);
        Assert.Equivalent(holder, JsonSerializer.Deserialize(json, TaskIdJsonTestContext.Default.TaskIdHolder));
    }

    [Theory]
    [InlineData("""{"Id":7}""")]
    [InlineData("""{"Id":"trailing\\"}""")]
    public void MalformedTaskIdsFailToDeserialize(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, TaskIdJsonTestContext.Default.TaskIdHolder));
}

public sealed record TaskIdHolder(TaskId Id, TaskId? Optional, Dictionary<TaskId, int> Counts);

[JsonSerializable(typeof(TaskIdHolder))]
internal sealed partial class TaskIdJsonTestContext : JsonSerializerContext;
