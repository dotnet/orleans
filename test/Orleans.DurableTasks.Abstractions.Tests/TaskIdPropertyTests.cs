using CsCheck;
using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public sealed class TaskIdPropertyTests
{
    [Fact]
    public void TaskIdFormattingParsingAndHierarchyLawsHold()
    {
        var text = Gen.Char.AlphaNumeric.Array[0, 8].Select(static characters => new string(characters));
        var segments = Gen.Select(
            text,
            text,
            text,
            text,
            static (first, second, third, fourth) => new[]
            {
                $"{first}/{second}",
                $@"{second}\{third}",
                third.Length == 0 ? "leaf" : third,
                fourth.Length == 0 ? "tail" : fourth,
            });

        segments.Sample(
            generatedSegments =>
            {
                var hierarchy = new List<TaskId> { TaskId.CreateRoot(generatedSegments[0]) };
                foreach (var segment in generatedSegments.AsSpan(1))
                {
                    hierarchy.Add(hierarchy[^1].Child(segment));
                }

                var taskId = hierarchy[^1];
                var canonical = string.Join("/", generatedSegments.Select(EscapeSegment));
                Assert.Equal(canonical, taskId.ToString());
                Assert.Contains(@"\/", canonical);
                Assert.Contains(@"\\", canonical);

                var parsedString = TaskId.Parse(canonical);
                var parsedSpan = TaskId.Parse(canonical.AsSpan());
                Assert.True(TaskId.TryParse(canonical, provider: null, out var triedString));
                Assert.True(TaskId.TryParse(canonical.AsSpan(), provider: null, out var triedSpan));
                Assert.Equal(taskId, parsedString);
                Assert.Equal(taskId, parsedSpan);
                Assert.Equal(taskId, triedString);
                Assert.Equal(taskId, triedSpan);
                Assert.Equal(taskId.GetHashCode(), parsedString.GetHashCode());

                for (var index = 1; index < hierarchy.Count; index++)
                {
                    Assert.Equal(hierarchy[index - 1], hierarchy[index].Parent());
                    Assert.True(hierarchy[index - 1].IsParentOf(hierarchy[index]));
                    Assert.True(hierarchy[index].IsChildOf(hierarchy[index - 1]));
                }

                Assert.True(hierarchy[0].IsAncestorOf(taskId));
                Assert.True(taskId.IsDescendantOf(hierarchy[0]));
                Assert.False(taskId.IsAncestorOf(hierarchy[0]));
                Assert.False(hierarchy[0].IsDescendantOf(taskId));

                var exact = new char[canonical.Length];
                Assert.True(taskId.TryFormat(exact, out var exactCharsWritten, default, provider: null));
                Assert.Equal(canonical.Length, exactCharsWritten);
                Assert.Equal(canonical, new string(exact));

                var shortBuffer = new char[canonical.Length - 1];
                Assert.False(taskId.TryFormat(shortBuffer, out var shortCharsWritten, default, provider: null));
                Assert.Equal(0, shortCharsWritten);
            },
            seed: "0N0XIzNsQ0O2",
            iter: 64,
            threads: 1,
            print: static value => string.Join(" | ", value));
    }

    private static string EscapeSegment(string segment)
        => segment.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("/", "\\/", StringComparison.Ordinal);
}
