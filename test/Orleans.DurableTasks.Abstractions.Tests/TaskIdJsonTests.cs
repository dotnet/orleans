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
    [InlineData("""{"Id":"\ud800"}""")]
    [InlineData("""{"Id":"","Counts":{"\udc00":1}}""")]
    public void MalformedTaskIdsFailToDeserialize(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, TaskIdJsonTestContext.Default.TaskIdHolder));

    [Fact]
    public void TaskIdsWithAnyWellFormedTextRoundTripAndUnpairedSurrogatesAreRejected()
    {
        // Characters near and inside the surrogate range, so segments hold pairs, unpaired halves, and ordinary text.
        var character = Gen.OneOf(Gen.Char['\uD7FE', '\uE001'], Gen.Char["ab/\\"]);
        var segment = character.Array[1, 4].Select(static characters => new string(characters));
        segment.Array[1, 3].Sample(
            segments =>
            {
                if (!segments.All(IsWellFormed))
                {
                    // Every way of building or parsing the identifier rejects malformed text, so no encoding replaces it.
                    var malformed = Array.Find(segments, static segment => !IsWellFormed(segment))!;
                    Assert.ThrowsAny<ArgumentException>(() => TaskId.CreateRoot(malformed));
                    Assert.ThrowsAny<ArgumentException>(() => TaskId.CreateRoot("root").Child(malformed));
                    Assert.False(TaskId.TryParse(malformed, out _));
                    Assert.Throws<FormatException>(() => TaskId.Parse(malformed));
                    return;
                }

                var taskId = TaskId.CreateRoot(segments[0]);
                foreach (var child in segments.AsSpan(1))
                {
                    taskId = taskId.Child(child);
                }

                // A value and a dictionary key both round-trip through JSON's UTF-8 encoding unchanged.
                var holder = new TaskIdHolder(taskId, taskId, new() { [taskId] = 1 });
                var json = JsonSerializer.Serialize(holder, TaskIdJsonTestContext.Default.TaskIdHolder);
                var read = JsonSerializer.Deserialize(json, TaskIdJsonTestContext.Default.TaskIdHolder)!;
                Assert.Equal(taskId, read.Id);
                Assert.Equal(taskId, Assert.Single(read.Counts).Key);
                Assert.Equal(taskId, TaskId.Parse(taskId.ToString()));
            },
            iter: 2000);

        static bool IsWellFormed(string text)
        {
            for (var index = 0; index < text.Length; index++)
            {
                if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    index++;
                }
                else if (char.IsSurrogate(text[index]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}

public sealed record TaskIdHolder(TaskId Id, TaskId? Optional, Dictionary<TaskId, int> Counts);

[JsonSerializable(typeof(TaskIdHolder))]
internal sealed partial class TaskIdJsonTestContext : JsonSerializerContext;
