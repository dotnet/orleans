using Orleans.Core.Internal;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public class DurableGrainTests(IntegrationTestFixture fixture) : IClassFixture<IntegrationTestFixture>
{
    private IGrainFactory Client => fixture.Client;

    [Fact]
    public async Task DurableGrain_State_Persistence_Test()
    {
        // Arrange
        var grain = Client.GetGrain<ITestDurableGrain>(Guid.NewGuid());

        // Act - Set state properties and persist
        await grain.SetTestValues("Test Name", 42);

        // Assert
        Assert.Equal("Test Name", await grain.GetName());
        Assert.Equal(42, await grain.GetCounter());

        // Force deactivation and get a new reference
        var idBefore = await grain.GetActivationId();
        await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();
        Assert.NotEqual(idBefore, await grain.GetActivationId());

        // Assert - State should be recovered
        Assert.Equal("Test Name", await grain.GetName());
        Assert.Equal(42, await grain.GetCounter());
    }

    [Fact]
    public async Task DurableGrain_Update_State_Test()
    {
        // Arrange
        var grain = Client.GetGrain<ITestDurableGrain>(Guid.NewGuid());

        // Act - Set state and persist
        await grain.SetTestValues("Initial Name", 10);

        // Update state and persist again
        await grain.SetTestValues("Updated Name", 20);

        // Assert
        Assert.Equal("Updated Name", await grain.GetName());
        Assert.Equal(20, await grain.GetCounter());

        // Force deactivation and get a new reference
        await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();

        // Assert - Updated state should be recovered
        Assert.Equal("Updated Name", await grain.GetName());
        Assert.Equal(20, await grain.GetCounter());
    }

    [Fact]
    public async Task DurableGrain_Complex_Types_Test()
    {
        // Arrange
        var grain = Client.GetGrain<ITestDurableGrainWithComplexState>(Guid.NewGuid());

        // Act - Set complex state and persist
        var person = new TestPerson { Id = 1, Name = "John Doe", Age = 30 };
        var items = new List<string> { "Item1", "Item2", "Item3" };
        await grain.SetTestValues(person, items);

        // Assert
        var retrievedPerson = await grain.GetPerson();
        var retrievedItems = await grain.GetItems();

        Assert.Equal("John Doe", retrievedPerson.Name);
        Assert.Equal(3, retrievedItems.Count);

        // Force deactivation and get a new reference
        var idBefore = await grain.GetActivationId();
        await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();
        Assert.NotEqual(idBefore, await grain.GetActivationId());

        // Assert - Complex state should be recovered
        retrievedPerson = await grain.GetPerson();
        retrievedItems = await grain.GetItems();

        Assert.NotNull(retrievedPerson);
        Assert.Equal(1, retrievedPerson.Id);
        Assert.Equal("John Doe", retrievedPerson.Name);
        Assert.Equal(30, retrievedPerson.Age);

        Assert.Equal(3, retrievedItems.Count);
        Assert.Equal("Item1", retrievedItems[0]);
        Assert.Equal("Item2", retrievedItems[1]);
        Assert.Equal("Item3", retrievedItems[2]);
    }

    [Fact]
    public async Task DurableGrain_Multiple_Collections_Test()
    {
        // Arrange
        var grain = Client.GetGrain<ITestMultiCollectionGrain>(Guid.NewGuid());

        // Act - Populate collections and persist
        await grain.AddToDictionary("key1", 1);
        await grain.AddToDictionary("key2", 2);
        await grain.AddToList("item1");
        await grain.AddToList("item2");
        await grain.AddToQueue(100);
        await grain.AddToQueue(200);
        await grain.AddToSet("set1");
        await grain.AddToSet("set2");

        // Assert
        Assert.Equal(2, await grain.GetDictionaryCount());
        Assert.Equal(2, await grain.GetListCount());
        Assert.Equal(2, await grain.GetQueueCount());
        Assert.Equal(2, await grain.GetSetCount());

        // Force deactivation and get a new reference
        var idBefore = await grain.GetActivationId();
        await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();
        Assert.NotEqual(idBefore, await grain.GetActivationId());

        // Assert - All collections should be recovered
        Assert.Equal(2, await grain.GetDictionaryCount());
        Assert.Equal(1, await grain.GetDictionaryValue("key1"));
        Assert.Equal(2, await grain.GetDictionaryValue("key2"));

        Assert.Equal(2, await grain.GetListCount());
        Assert.Equal("item1", await grain.GetListItem(0));
        Assert.Equal("item2", await grain.GetListItem(1));

        Assert.Equal(2, await grain.GetQueueCount());
        Assert.Equal(100, await grain.PeekQueueItem());

        Assert.Equal(2, await grain.GetSetCount());
        Assert.True(await grain.ContainsSetItem("set1"));
        Assert.True(await grain.ContainsSetItem("set2"));
    }

    [Fact]
    public async Task DurableGrain_State_Modifications_Test()
    {
        // Arrange
        var grain = Client.GetGrain<ITestMultiCollectionGrain>(Guid.NewGuid());

        // Act - Populate initial state and persist
        await grain.AddToDictionary("key1", 1);
        await grain.AddToList("item1");
        await grain.AddToQueue(100);
        await grain.AddToSet("set1");

        // Modify state and persist again
        await grain.AddToDictionary("key2", 2);
        await grain.AddToDictionary("key1", 10); // Update via interface method
        await grain.AddToList("item2");
        await grain.AddToQueue(200);
        await grain.AddToSet("set2");

        // Assert
        Assert.Equal(2, await grain.GetDictionaryCount());
        Assert.Equal(10, await grain.GetDictionaryValue("key1"));
        Assert.Equal(2, await grain.GetListCount());
        Assert.Equal(2, await grain.GetQueueCount());
        Assert.Equal(2, await grain.GetSetCount());

        // Force deactivation and get a new reference
        var idBefore = await grain.GetActivationId();
        await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();
        Assert.NotEqual(idBefore, await grain.GetActivationId());

        // Assert - Modified state should be recovered
        Assert.Equal(2, await grain.GetDictionaryCount());
        Assert.Equal(10, await grain.GetDictionaryValue("key1"));
        Assert.Equal(2, await grain.GetDictionaryValue("key2"));

        Assert.Equal(2, await grain.GetListCount());
        Assert.Equal("item1", await grain.GetListItem(0));
        Assert.Equal("item2", await grain.GetListItem(1));

        // Further modify the state
        await grain.RemoveFromDictionary("key1");
        await grain.RemoveListItemAt(0);
        await grain.DequeueItem();
        await grain.RemoveFromSet("set1");

        // Assert the modifications
        Assert.Equal(1, await grain.GetDictionaryCount());
        Assert.Equal(1, await grain.GetListCount());
        Assert.Equal(1, await grain.GetQueueCount());
        Assert.Equal(1, await grain.GetSetCount());
    }

    [Fact]
    public async Task Grain_State_Should_Persist_Between_Activations()
    {
        // Arrange - Get a reference to a grain
        var grain = Client.GetGrain<ITestDurableGrainInterface>(Guid.NewGuid());

        // Act - Set the grain state
        await grain.SetValues("Test Name", 42);
        var initialState = await grain.GetValues();

        // Deactivate the grain forcefully
        var idBefore = await grain.GetActivationId();
        await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();
        Assert.NotEqual(idBefore, await grain.GetActivationId());

        // Get the values from the grain (which will be reactivated)
        var newState = await grain.GetValues();

        // Assert
        Assert.Equal(initialState.Name, newState.Name);
        Assert.Equal(initialState.Counter, newState.Counter);
    }

    [Fact]
    public async Task Grain_Should_Handle_Multiple_Collections()
    {
        // Arrange
        var grain = Client.GetGrain<ITestMultiCollectionGrain>(Guid.NewGuid());

        // Act - Add items to collections
        await grain.AddToDictionary("key1", 1);
        await grain.AddToDictionary("key2", 2);

        await grain.AddToList("item1");
        await grain.AddToList("item2");

        await grain.AddToQueue(100);
        await grain.AddToQueue(200);

        await grain.AddToSet("set1");
        await grain.AddToSet("set2");
        await grain.AddToSet("set1"); // Duplicate, should be ignored

        // Assert - Check counts
        Assert.Equal(2, await grain.GetDictionaryCount());
        Assert.Equal(2, await grain.GetListCount());
        Assert.Equal(2, await grain.GetQueueCount());
        Assert.Equal(2, await grain.GetSetCount());

        // Deactivate the grain forcefully
        var idBefore = await grain.GetActivationId();
        await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();
        Assert.NotEqual(idBefore, await grain.GetActivationId());

        // Assert - Check values after reactivation
        Assert.Equal(1, await grain.GetDictionaryValue("key1"));
        Assert.Equal(2, await grain.GetDictionaryValue("key2"));
        Assert.Equal("item1", await grain.GetListItem(0));
        Assert.Equal("item2", await grain.GetListItem(1));
        Assert.Equal(100, await grain.PeekQueueItem());
        Assert.True(await grain.ContainsSetItem("set1"));
        Assert.True(await grain.ContainsSetItem("set2"));

        // Act - Modify collections
        await grain.RemoveFromDictionary("key1");
        await grain.RemoveListItemAt(0);
        await grain.DequeueItem();
        await grain.RemoveFromSet("set1");

        // Assert - Check counts after modifications
        Assert.Equal(1, await grain.GetDictionaryCount());
        Assert.Equal(1, await grain.GetListCount());
        Assert.Equal(1, await grain.GetQueueCount());
        Assert.Equal(1, await grain.GetSetCount());

        // Deactivate the grain again
        idBefore = await grain.GetActivationId();
        await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();
        Assert.NotEqual(idBefore, await grain.GetActivationId());

        // Assert - Check values after second reactivation
        Assert.Equal(1, await grain.GetDictionaryCount());
        Assert.Equal(1, await grain.GetListCount());
        Assert.Equal(1, await grain.GetQueueCount());
        Assert.Equal(1, await grain.GetSetCount());
        Assert.Equal(2, await grain.GetDictionaryValue("key2"));
        Assert.Equal("item2", await grain.GetListItem(0));
        Assert.Equal(200, await grain.PeekQueueItem());
        Assert.True(await grain.ContainsSetItem("set2"));
    }

    [Fact]
    public async Task Grain_Should_Handle_Large_State()
    {
        // Arrange
        var grain = Client.GetGrain<ITestMultiCollectionGrain>(Guid.NewGuid());

        // Act - Add many items
        const int itemCount = 1000;
        for (int i = 0; i < itemCount; i++)
        {
            await grain.AddToDictionary($"key{i}", i);
            if (i < 100) // Add fewer items to other collections to keep test runtime reasonable
            {
                await grain.AddToList($"item{i}");
                await grain.AddToQueue(i);
                await grain.AddToSet($"set{i}");
            }
        }

        // Assert - Check counts
        Assert.Equal(itemCount, await grain.GetDictionaryCount());
        Assert.Equal(100, await grain.GetListCount());
        Assert.Equal(100, await grain.GetQueueCount());
        Assert.Equal(100, await grain.GetSetCount());

        // Deactivate the grain forcefully
        var idBefore = await grain.GetActivationId();
        await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();
        Assert.NotEqual(idBefore, await grain.GetActivationId());

        // Assert - Check random values after reactivation
        for (int i = 0; i < 10; i++)
        {
            var randomIndex = new Random().Next(0, itemCount - 1);
            Assert.Equal(randomIndex, await grain.GetDictionaryValue($"key{randomIndex}"));

            if (randomIndex < 100)
            {
                Assert.Equal($"item{randomIndex}", await grain.GetListItem(randomIndex));
                Assert.True(await grain.ContainsSetItem($"set{randomIndex}"));
            }
        }
    }
}
