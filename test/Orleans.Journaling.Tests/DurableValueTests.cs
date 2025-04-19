using Microsoft.Extensions.Logging;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public class DurableValueTests : StateMachineTestBase
{
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
