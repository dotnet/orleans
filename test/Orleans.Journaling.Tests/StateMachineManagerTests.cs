using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for StateMachineManager, the core component of Orleans' journaling infrastructure.
/// 
/// StateMachineManager coordinates multiple durable data structures (DurableDictionary, DurableList, etc.)
/// within a single grain, ensuring that all state changes are atomically journaled and can be
/// recovered together. It manages the lifecycle of state machines, handles persistence through
/// WriteStateAsync calls, and ensures consistent recovery after failures.
/// </summary>
[TestCategory("BVT")]
public class StateMachineManagerTests : StateMachineTestBase
{
    /// <summary>
    /// Tests the registration and basic operation of multiple state machines.
    /// Verifies that different types of durable collections can be registered
    /// with the manager and operate independently.
    /// </summary>
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

    /// <summary>
    /// Tests that all registered state machines are correctly recovered together.
    /// Verifies that the manager maintains consistency across multiple collections
    /// during recovery from persisted state.
    /// </summary>
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

    /// <summary>
    /// Tests multiple WriteStateAsync calls with operations in between.
    /// Verifies that each WriteStateAsync creates a consistent checkpoint
    /// and that the final state is correctly recovered.
    /// </summary>
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

    /// <summary>
    /// Tests managing multiple state machines of different types simultaneously.
    /// Verifies that the manager correctly handles diverse data structures
    /// (dictionaries with different key/value types, lists, values) in a single grain.
    /// </summary>
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

    /// <summary>
    /// Tests that multiple state machines can operate independently without interference.
    /// Verifies namespace isolation between different state machines with similar keys.
    /// </summary>
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

    /// <summary>
    /// Stress test for state recovery with large amounts of data.
    /// Verifies that the journaling system can handle and recover state machines
    /// containing thousands of entries without data loss or corruption.
    /// </summary>
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

    /// <summary>
    /// Tests the full lifecycle of a retired state machine. It is preserved and also reintroduced through an
    /// early compaction, but purged eventually after its grace period expires on later compactions.
    /// </summary>
    [Fact]
    public async Task StateMachineManager_AutoRetiringStateMachines()
    {
        const string DictToKeepKey = "dictToKeep";
        const string DictToRetireKey = "dictToRetire";

        var period = ManagerOptions.RetirementGracePeriod;
        var timeProvider = new FakeTimeProvider(DateTime.UtcNow);
        var storage = CreateStorage();

        // -------------- STEP 1 --------------

        // We begin with 2 dictionaries, one of which we will retire by means of not registering it in the manager.
        // This would be in the real-world developers removing it from the grain's ctor as a dependency.
        var sut1 = CreateTestSystem(storage, timeProvider);
        var dictToKeep1 = CreateTestMachine(DictToKeepKey, sut1.Manager);
        var dictToRetire2 = CreateTestMachine(DictToRetireKey, sut1.Manager);

        await sut1.Lifecycle.OnStart();

        dictToKeep1.Add("a", 1);
        dictToRetire2.Add("b", 1);

        await sut1.Manager.WriteStateAsync(CancellationToken.None);

        // -------------- STEP 2 --------------

        // This time, we only register the dictionary we want to keep, this marks dictToRetire as retired.
        var sut2 = CreateTestSystem(storage, timeProvider);
        var dictToKeep2 = CreateTestMachine(DictToKeepKey, sut2.Manager);

        await sut2.Lifecycle.OnStart();
        
        // The manager should have recovered the state for dictToKeep,
        // and created a DurableNothing placeholder for dictToRetire (we cant test for it at this point). 
        Assert.Equal(1, dictToKeep2["a"]);

        // We advance time by half the grace period to see if we can save it from purging.
        timeProvider.Advance(period / 2);

        await TriggerCompaction(sut2.Manager, dictToKeep2);

        // -------------- STEP 3 --------------

        // Verify that the retired dictionary was NOT purged by this compaction, as only half the time has passed.
        var sut3 = CreateTestSystem(storage, timeProvider);
        var dictToKeep3 = CreateTestMachine(DictToKeepKey, sut3.Manager);
        var dictToRetire3 = CreateTestMachine(DictToRetireKey, sut3.Manager);

        await sut3.Lifecycle.OnStart();

        Assert.Equal(10, dictToKeep3["a"]);

        // The fact this entry ["b", 1] exists proves that the state of dictToRetire was preserved, even though we did not register it in step 2.
        Assert.Equal(1, dictToRetire3["b"]);

        // By advancing time by another half-period we cover the full period. But since we have re-introduced dictToRetire, we should have un-retired it.
        // This is similar to step 2, but there we avoided purging due to time not being due, whereas here we avoid purging due to re-registration.
        timeProvider.Advance(period / 2);

        await TriggerCompaction(sut3.Manager, dictToKeep3);

        // -------------- STEP 4 --------------

        // Because of re-registration is step 3 (to test it was not purged), this means dictToRetire has been removed from the tracker.
        // Again as in step 2, we only register the dictionary we want to keep, this marks dictToRetire as retired.
        var sut4 = CreateTestSystem(storage, timeProvider);
        var dictToKeep4 = CreateTestMachine(DictToKeepKey, sut4.Manager);

        await sut4.Lifecycle.OnStart();

        // The manager should have recovered the state for dictToKeep.
        // It should have created a DurableNothing placeholder for dictToRetire, but we can not test for that.


        // This time we advance time to cover the full period. Note that this is necessary because a side effect of step 3
        // was that dictToRetire was removed from the tracker (since it came back), so just triggering a compaction won't cut it
        // as time to retire will essentially be reset to "now".
        timeProvider.Advance(period);

        // This compaction should finally purge it.
        await TriggerCompaction(sut4.Manager, dictToKeep4);

        // -------------- STEP 5 --------------

        // At this point, the manager has performed a snapshot, so it should have purged the dictToRetire data.
        // By registering both dictionaries again, we should see what state remains after the snapshot.
        var sut5 = CreateTestSystem(storage, timeProvider);
        var dictToKeep5 = CreateTestMachine(DictToKeepKey, sut5.Manager);
        var dictToRetire5 = CreateTestMachine(DictToRetireKey, sut5.Manager);

        await sut5.Lifecycle.OnStart();
        Assert.Equal(10, dictToKeep5["a"]);

        // The retired dictionary should now be empty because its state was purged during the compaction.
        // Note that this is a new version of dictToRetire, since the original was removed. Idea here is
        // that if we can register a new dictToRetire (with the same key), it means that the machine itself
        // has been removed but also the data, otherwise a previous machine would have had at least one
        // entry i.e. ["b", 1].

        Assert.Empty(dictToRetire5);

        // Note: The retirement of state machines has the nice benefit of being able to reuse machine names.

        DurableDictionary<string, int> CreateTestMachine(string key, IStateMachineManager manager) =>
            new(key, manager, CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool);

        static async Task TriggerCompaction(IStateMachineManager manager, DurableDictionary<string, int> dict)
        {
            for (var i = 0; i < 11; i++)
            {
                dict["a"] = i;
                await manager.WriteStateAsync(CancellationToken.None);
            }
        }
    }
}
