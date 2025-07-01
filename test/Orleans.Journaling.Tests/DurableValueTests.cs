using Microsoft.Extensions.Logging;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for DurableValue, a simple persistent value wrapper that uses Orleans' journaling
/// infrastructure to maintain a single value across grain activations and system restarts.
/// 
/// DurableValue is the simplest durable data structure, providing a way to persist
/// a single value of any type. It's useful for grain state properties that need
/// durability without the complexity of collections.
/// </summary>
[TestCategory("BVT")]
public class DurableValueTests : StateMachineTestBase
{
    /// <summary>
    /// Tests basic value operations: setting and updating values.
    /// Verifies that value changes are properly journaled and persisted.
    /// </summary>
    [Fact]
    public async Task DurableValue_BasicOperations_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<string>();
        var durableValue = new DurableValue<string>("testValue", manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();

        // Act - Set initial value
        durableValue.Value = "Hello World";
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Hello World", durableValue.Value);

        // Act - Update value
        durableValue.Value = "Updated Value";
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Updated Value", durableValue.Value);
    }

    /// <summary>
    /// Tests that value state is correctly persisted and recovered.
    /// Creates a DurableValue, sets a value, then recreates it from the same
    /// storage to verify state recovery.
    /// </summary>
    [Fact]
    public async Task DurableValue_Persistence_Test()
    {
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<int>();
        var durableValue = new DurableValue<int>("counter", manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();

        // Act - Modify and persist
        durableValue.Value = 42;
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Create a new manager with the same storage
        var sut2 = CreateTestSystem(storage: sut.Storage);
        var durableValue2 = new DurableValue<int>("counter", sut2.Manager, codec, SessionPool);
        await sut2.Lifecycle.OnStart();

        // Assert - Value should be recovered
        Assert.Equal(42, durableValue2.Value);
    }

    /// <summary>
    /// Tests handling of null values in DurableValue.
    /// Verifies that null values are properly persisted and can be
    /// distinguished from uninitialized state.
    /// </summary>
    [Fact]
    public async Task DurableValue_NullValue_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<string?>();
        var durableValue = new DurableValue<string?>("nullableValue", manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();

        // Act - Set to null
        durableValue.Value = null;
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert
        Assert.Null(durableValue.Value);

        // Act - Update to non-null
        durableValue.Value = "Not null anymore";
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Not null anymore", durableValue.Value);

        // Act - Update back to null
        durableValue.Value = null;
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert
        Assert.Null(durableValue.Value);
    }

    /// <summary>
    /// Tests that DurableValue correctly handles complex value types.
    /// Verifies that custom objects are properly serialized and that
    /// mutations to properties of the stored object are persisted.
    /// </summary>
    [Fact]
    public async Task DurableValue_ComplexType_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<TestPerson>();
        var durableValue = new DurableValue<TestPerson>("person", manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();

        // Act
        var person = new TestPerson { Id = 1, Name = "John Doe", Age = 30 };
        durableValue.Value = person;
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(durableValue.Value);
        Assert.Equal(1, durableValue.Value.Id);
        Assert.Equal("John Doe", durableValue.Value.Name);
        Assert.Equal(30, durableValue.Value.Age);

        // Act - Update property
        durableValue.Value.Age = 31;
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert
        Assert.Equal(31, durableValue.Value.Age);
    }
}
