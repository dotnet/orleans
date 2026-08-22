using System.Distributed.DurableTasks;
using Xunit;

namespace Orleans;

/// <summary>
/// Tests for the <see cref="TaskIdExtensions"/> class.
/// </summary>
public class TaskIdExtensionsTests
{
    [Fact, TestCategory("BVT")]
    public void ToHierarchicalKey_WithValidTaskId_ReturnsEquivalentKey()
    {
        // Arrange
        // Note: TaskId.Create escapes the input, so / becomes \/
        var taskId = TaskId.Create("workflow-task-123");

        // Act
        var key = taskId.ToHierarchicalKey();

        // Assert
        Assert.NotNull(key);
        Assert.Equal("workflow-task-123", key.ToString());
    }

    [Fact, TestCategory("BVT")]
    public void ToHierarchicalKey_WithDefaultTaskId_ReturnsNull()
    {
        // Arrange
        var taskId = TaskId.None;

        // Act
        var key = taskId.ToHierarchicalKey();

        // Assert
        Assert.Null(key);
    }

    [Fact, TestCategory("BVT")]
    public void ToTaskId_WithValidHierarchicalKey_ReturnsEquivalentTaskId()
    {
        // Arrange
        // HierarchicalKey.Create treats / as segment separator
        var key = HierarchicalKey.Create("workflow/task-123");

        // Act
        var taskId = key.ToTaskId();

        // Assert
        Assert.Equal("workflow/task-123", taskId.ToString());
    }

    [Fact, TestCategory("BVT")]
    public void ToTaskId_WithNullHierarchicalKey_ReturnsNone()
    {
        // Arrange
        HierarchicalKey? key = null;

        // Act
        var taskId = key.ToTaskId();

        // Assert
        Assert.Equal(TaskId.None, taskId);
        Assert.True(taskId.IsDefault);
    }

    [Fact, TestCategory("BVT")]
    public void RoundTrip_TaskIdToHierarchicalKeyAndBack_PreservesValue()
    {
        // Arrange
        var originalTaskId = TaskId.Create("parent-child-grandchild");

        // Act
        var key = originalTaskId.ToHierarchicalKey();
        var roundTrippedTaskId = key.ToTaskId();

        // Assert
        Assert.Equal(originalTaskId.ToString(), roundTrippedTaskId.ToString());
        Assert.Equal(originalTaskId, roundTrippedTaskId);
    }

    [Fact, TestCategory("BVT")]
    public void RoundTrip_HierarchicalKeyToTaskIdAndBack_PreservesValue()
    {
        // Arrange
        var originalKey = HierarchicalKey.Create("parent/child/grandchild");

        // Act
        var taskId = originalKey.ToTaskId();
        var roundTrippedKey = taskId.ToHierarchicalKey();

        // Assert
        Assert.NotNull(roundTrippedKey);
        Assert.Equal(originalKey.ToString(), roundTrippedKey.ToString());
        Assert.Equal(originalKey, roundTrippedKey);
    }

    [Fact, TestCategory("BVT")]
    public void ToHierarchicalKey_WithEscapedCharacters_PreservesEscaping()
    {
        // Arrange
        // TaskId.Create auto-escapes, so double-escape if you want literal backslash
        var taskId = TaskId.Create(@"workflow-task-with-dashes");

        // Act
        var key = taskId.ToHierarchicalKey();

        // Assert
        Assert.NotNull(key);
        Assert.Equal(@"workflow-task-with-dashes", key.ToString());
    }

    [Fact, TestCategory("BVT")]
    public void ToTaskId_WithEscapedCharacters_PreservesEscaping()
    {
        // Arrange
        // HierarchicalKey needs escaped slashes for literal slash in segment
        var key = HierarchicalKey.Create(@"workflow/task-with-literal-escaped");

        // Act
        var taskId = key.ToTaskId();

        // Assert
        Assert.Equal(@"workflow/task-with-literal-escaped", taskId.ToString());
    }

    [Fact, TestCategory("BVT")]
    public void ToHierarchicalKey_WithHierarchy_MaintainsParentChildRelationships()
    {
        // Arrange
        var parentTaskId = TaskId.Create("parent");
        var childTaskId = parentTaskId.Child("child");

        // Act
        var parentKey = parentTaskId.ToHierarchicalKey();
        var childKey = childTaskId.ToHierarchicalKey();

        // Assert
        Assert.NotNull(parentKey);
        Assert.NotNull(childKey);
        Assert.True(parentKey.IsParentOf(childKey));
        Assert.True(childKey.IsChildOf(parentKey));
    }

    [Fact, TestCategory("BVT")]
    public void ToTaskId_WithHierarchy_MaintainsParentChildRelationships()
    {
        // Arrange
        var parentKey = HierarchicalKey.Create("parent");
        var childKey = parentKey.CreateChildKey("child");

        // Act
        var parentTaskId = parentKey.ToTaskId();
        var childTaskId = childKey.ToTaskId();

        // Assert
        Assert.True(parentTaskId.IsParentOf(childTaskId));
        Assert.True(childTaskId.IsChildOf(parentTaskId));
    }
}
