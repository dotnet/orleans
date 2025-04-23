using Microsoft.Extensions.Logging;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public class DurableSetTests : StateMachineTestBase
{
    [Fact]
    public async Task DurableSet_BasicOperations_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<string>();
        var set = new DurableSet<string>("testSet", manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Act - Add items
        bool added1 = set.Add("one");
        bool added2 = set.Add("two");
        bool added3 = set.Add("three");
        bool duplicateAdded = set.Add("one"); // Adding duplicate
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.True(added1);
        Assert.True(added2);
        Assert.True(added3);
        Assert.False(duplicateAdded); // Should not add duplicates
        Assert.Equal(3, set.Count);
        Assert.Contains("one", set);
        Assert.Contains("two", set);
        Assert.Contains("three", set);
        
        // Act - Remove item
        bool removed = set.Remove("two");
        bool removedNonExisting = set.Remove("four"); // Remove non-existing
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.True(removed);
        Assert.False(removedNonExisting);
        Assert.Equal(2, set.Count);
        Assert.Contains("one", set);
        Assert.DoesNotContain("two", set);
        Assert.Contains("three", set);
    }
    
    [Fact]
    public async Task DurableSet_Persistence_Test()
    {
        // First manager and set
        var sut = CreateTestSystem();
        var codec = CodecProvider.GetCodec<string>();
        var set1 = new DurableSet<string>("testSet", sut.Manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Act - Add items and persist
        set1.Add("one");
        set1.Add("two");
        set1.Add("three");
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Create a new manager with the same storage
        var sut2 = CreateTestSystem(storage: sut.Storage);
        var set2 = new DurableSet<string>("testSet", sut2.Manager, codec, SessionPool);
        await sut2.Lifecycle.OnStart();

        // Assert - Set should be recovered
        Assert.Equal(3, set2.Count);
        Assert.Contains("one", set2);
        Assert.Contains("two", set2);
        Assert.Contains("three", set2);
    }
    
    [Fact]
    public async Task DurableSet_ComplexValues_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<TestPerson>();
        var set = new DurableSet<TestPerson>("personSet", manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Act
        var person1 = new TestPerson { Id = 1, Name = "John", Age = 30 };
        var person2 = new TestPerson { Id = 2, Name = "Jane", Age = 25 };
        var person3 = new TestPerson { Id = 1, Name = "John", Age = 30 }; // Same as person1
        
        set.Add(person1);
        set.Add(person2);
        bool duplicateAdded = set.Add(person3); // Should not add duplicate when overriding Equals
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(2, set.Count);
        Assert.Contains(person1, set);
        Assert.Contains(person2, set);
    }
    
    [Fact]
    public async Task DurableSet_Clear_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<string>();
        var set = new DurableSet<string>("clearSet", manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Add items
        set.Add("one");
        set.Add("two");
        set.Add("three");
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Act - Clear
        set.Clear();
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Empty(set);
        Assert.Empty(set);
    }
    
    [Fact]
    public async Task DurableSet_Enumeration_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<string>();
        var set = new DurableSet<string>("enumSet", manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Add items
        var expectedItems = new HashSet<string> { "one", "two", "three" };
        
        foreach (var item in expectedItems)
        {
            set.Add(item);
        }
        
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Act
        var actualItems = set.ToHashSet();
        
        // Assert
        Assert.Equal(expectedItems, actualItems);
    }
    
    [Fact]
    public async Task DurableSet_LargeNumberOfItems_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<int>();
        var set = new DurableSet<int>("largeSet", manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Act - Add many items
        const int itemCount = 1000;
        for (int i = 0; i < itemCount; i++)
        {
            set.Add(i);
        }
        
        // Add some duplicates which should be ignored
        for (int i = 0; i < 100; i++)
        {
            set.Add(i);
        }
        
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(itemCount, set.Count);

        // Create a new manager with the same storage to test recovery
        var sut2 = CreateTestSystem(storage: sut.Storage);
        var set2 = new DurableSet<int>("largeSet", sut2.Manager, codec, SessionPool);
        await sut2.Lifecycle.OnStart();
        
        // Assert - Large set is correctly recovered
        Assert.Equal(itemCount, set2.Count);
        for (int i = 0; i < itemCount; i++)
        {
            Assert.Contains(i, set2);
        }
    }
    
    [Fact]
    public async Task DurableSet_SetOperations_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<int>();
        var set1 = new DurableSet<int>("set1", manager, codec, SessionPool);
        var set2 = new DurableSet<int>("set2", manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Populate set1 with even numbers from 0 to 10
        for (int i = 0; i <= 10; i += 2)
        {
            set1.Add(i);
        }
        
        // Populate set2 with numbers from 5 to 15
        for (int i = 5; i <= 15; i++)
        {
            set2.Add(i);
        }
        
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Act & Assert - Set operations
        var set1HashSet = set1.ToHashSet();
        var set2HashSet = set2.ToHashSet();
        
        // Intersection
        var intersection = new HashSet<int>(set1HashSet);
        intersection.IntersectWith(set2HashSet);
        Assert.Equal(new HashSet<int> { 6, 8, 10 }, intersection);
        
        // Union
        var union = new HashSet<int>(set1HashSet);
        union.UnionWith(set2HashSet);
        Assert.Equal(new HashSet<int> { 0, 2, 4, 6, 8, 10, 5, 7, 9, 11, 12, 13, 14, 15 }, union);
        
        // Difference (set1 - set2)
        var difference = new HashSet<int>(set1HashSet);
        difference.ExceptWith(set2HashSet);
        Assert.Equal(new HashSet<int> { 0, 2, 4 }, difference);
    }
    
    [Fact]
    public async Task DurableSet_ExceptWith_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<int>();
        var set = new DurableSet<int>("exceptSet", manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Add numbers from 0 to 9
        for (int i = 0; i < 10; i++)
        {
            set.Add(i);
        }
        
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Act - Remove even numbers
        var evens = new List<int>();
        for (int i = 0; i < 10; i += 2)
        {
            evens.Add(i);
        }
        
        foreach (var even in evens)
        {
            set.Remove(even);
        }
        
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert - Should only contain odd numbers
        Assert.Equal(5, set.Count);
        for (int i = 1; i < 10; i += 2)
        {
            Assert.Contains(i, set);
        }
    }
}
