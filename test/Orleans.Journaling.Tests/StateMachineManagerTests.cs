using Microsoft.Extensions.Logging;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public class StateMachineManagerTests : StateMachineTestBase
{
    [Fact]
    public async Task StateMachineManager_RegisterStateMachine_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<int>();

        // Act - Register state machines
        var dictionary = new DurableDictionary<string, int>("dict1", manager, CodecProvider.GetCodec<string>(), codec, SessionPool);
        var list = new DurableList<string>("list1", manager, CodecProvider.GetCodec<string>(), SessionPool);
        var queue = new DurableQueue<int>("queue1", manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();

        // Add some data
        dictionary.Add("key1", 1);
        list.Add("item1");
        queue.Enqueue(42);

        // Write state
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert - Data is correctly stored
        Assert.Equal(1, dictionary["key1"]);
        Assert.Equal("item1", list[0]);
        Assert.Equal(42, queue.Peek());
    }

    [Fact]
    public async Task StateMachineManager_StateRecovery_Test()
    {
        // Arrange
        var sut = CreateTestSystem();

        // Create and populate state machines
        var dictionary = new DurableDictionary<string, int>("dict1", sut.Manager, CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool);
        var list = new DurableList<string>("list1", sut.Manager, CodecProvider.GetCodec<string>(), SessionPool);
        await sut.Lifecycle.OnStart();

        dictionary.Add("key1", 1);
        dictionary.Add("key2", 2);
        list.Add("item1");
        list.Add("item2");

        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Act - Create new manager with same storage
        var sut2 = CreateTestSystem(storage: sut.Storage);
        var recoveredDict = new DurableDictionary<string, int>("dict1", sut2.Manager, CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool);
        var recoveredList = new DurableList<string>("list1", sut2.Manager, CodecProvider.GetCodec<string>(), SessionPool);
        await sut2.Lifecycle.OnStart();

        // Assert - State should be recovered
        Assert.Equal(2, recoveredDict.Count);
        Assert.Equal(1, recoveredDict["key1"]);
        Assert.Equal(2, recoveredDict["key2"]);

        Assert.Equal(2, recoveredList.Count);
        Assert.Equal("item1", recoveredList[0]);
        Assert.Equal("item2", recoveredList[1]);
    }

    [Fact]
    public async Task StateMachineManager_MultipleWriteStates_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var dictionary = new DurableDictionary<string, int>("dict1", sut.Manager, CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool);
        await sut.Lifecycle.OnStart();

        // Act - Multiple operations with WriteState in between
        dictionary.Add("key1", 1);
        await manager.WriteStateAsync(CancellationToken.None);

        dictionary.Add("key2", 2);
        await manager.WriteStateAsync(CancellationToken.None);

        dictionary["key1"] = 10;
        await manager.WriteStateAsync(CancellationToken.None);

        dictionary.Remove("key2");
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert - Final state is correct
        Assert.Single(dictionary);
        Assert.Equal(10, dictionary["key1"]);
        Assert.False(dictionary.ContainsKey("key2"));

        // Create new manager to verify recovery
        var sut2 = CreateTestSystem(storage: sut.Storage);
        var recoveredDict = new DurableDictionary<string, int>("dict1", sut2.Manager, CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool);
        await sut2.Lifecycle.OnStart();

        // Assert - Recovery should have final state
        Assert.Single(recoveredDict);
        Assert.Equal(10, recoveredDict["key1"]);
        Assert.False(recoveredDict.ContainsKey("key2"));
    }

    [Fact]
    public async Task StateMachineManager_MultipleStateMachines_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;

        // Create multiple state machines with different types
        var intDict = new DurableDictionary<int, string>("intDict", manager, CodecProvider.GetCodec<int>(), CodecProvider.GetCodec<string>(), SessionPool);
        var stringList = new DurableList<string>("stringList", manager, CodecProvider.GetCodec<string>(), SessionPool);
        var personValue = new DurableValue<TestPerson>("personValue", manager, CodecProvider.GetCodec<TestPerson>(), SessionPool);
        await sut.Lifecycle.OnStart();

        // Act - Populate all state machines
        intDict.Add(1, "one");
        intDict.Add(2, "two");

        stringList.Add("item1");
        stringList.Add("item2");

        personValue.Value = new TestPerson { Id = 100, Name = "Test Person", Age = 30 };

        await manager.WriteStateAsync(CancellationToken.None);

        // Assert - All should have correct values
        Assert.Equal(2, intDict.Count);
        Assert.Equal("one", intDict[1]);

        Assert.Equal(2, stringList.Count);
        Assert.Equal("item1", stringList[0]);

        Assert.NotNull(personValue.Value);
        Assert.Equal(100, personValue.Value.Id);
        Assert.Equal("Test Person", personValue.Value.Name);

        // Create new manager to verify recovery of multiple state machines
        var sut2 = CreateTestSystem(storage: sut.Storage);
        var recoveredIntDict = new DurableDictionary<int, string>("intDict", sut2.Manager, CodecProvider.GetCodec<int>(), CodecProvider.GetCodec<string>(), SessionPool);
        var recoveredStringList = new DurableList<string>("stringList", sut2.Manager, CodecProvider.GetCodec<string>(), SessionPool);
        var recoveredPersonValue = new DurableValue<TestPerson>("personValue", sut2.Manager, CodecProvider.GetCodec<TestPerson>(), SessionPool);
        await sut2.Lifecycle.OnStart();

        // Assert - All should be recovered with correct values
        Assert.Equal(2, recoveredIntDict.Count);
        Assert.Equal("one", recoveredIntDict[1]);

        Assert.Equal(2, recoveredStringList.Count);
        Assert.Equal("item1", recoveredStringList[0]);

        Assert.NotNull(recoveredPersonValue.Value);
        Assert.Equal(100, recoveredPersonValue.Value.Id);
        Assert.Equal("Test Person", recoveredPersonValue.Value.Name);
    }

    [Fact]
    public async Task StateMachineManager_Concurrency_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var dict1 = new DurableDictionary<string, int>("dict1", manager, CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool);
        var dict2 = new DurableDictionary<string, int>("dict2", manager, CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool);
        await sut.Lifecycle.OnStart();

        // Act - Simulate concurrent operations on different state machines
        dict1.Add("key1", 1);
        dict2.Add("key1", 100);

        dict1.Add("key2", 2);
        dict2.Add("key2", 200);

        await manager.WriteStateAsync(CancellationToken.None);

        // Assert - Both state machines should have their correct values
        Assert.Equal(2, dict1.Count);
        Assert.Equal(2, dict2.Count);

        Assert.Equal(1, dict1["key1"]);
        Assert.Equal(100, dict2["key1"]);

        Assert.Equal(2, dict1["key2"]);
        Assert.Equal(200, dict2["key2"]);
    }

    [Fact]
    public async Task StateMachineManager_LargeStateRecovery_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var largeDict = new DurableDictionary<int, string>("largeDict", sut.Manager, CodecProvider.GetCodec<int>(), CodecProvider.GetCodec<string>(), SessionPool);
        await sut.Lifecycle.OnStart();

        // Act - Add many items
        const int itemCount = 1000;
        for (int i = 0; i < itemCount; i++)
        {
            largeDict.Add(i, $"Value {i}");
        }

        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Create new manager for recovery
        var sut2 = CreateTestSystem(storage: sut.Storage);
        var recoveredDict = new DurableDictionary<int, string>("largeDict", sut2.Manager, CodecProvider.GetCodec<int>(), CodecProvider.GetCodec<string>(), SessionPool);
        await sut2.Lifecycle.OnStart();

        // Assert - All items should be recovered
        Assert.Equal(itemCount, recoveredDict.Count);
        for (int i = 0; i < itemCount; i++)
        {
            Assert.Equal($"Value {i}", recoveredDict[i]);
        }
    }
}
